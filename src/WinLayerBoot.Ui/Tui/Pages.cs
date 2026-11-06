using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WinLayerBoot.Core;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgCheckState = Terminal.Gui.Views.CheckState;
using CoreCheckState = WinLayerBoot.Core.CheckState;

namespace WinLayerBoot.Tui;

internal enum Tone { Normal, Dim, Ok, Warn, Fail, Title }

internal sealed class LinesView : View
{
    private List<(string Text, Tone Tone)> _lines = [];
    private List<(string Text, Tone Tone)> _wrapped = [];
    private int _wrapWidth = -1;

    public LinesView()
    {
        CanFocus = true;
        VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;
        AddCommand(Command.Up, () => { ScrollVertical(-1); return true; });
        AddCommand(Command.Down, () => { ScrollVertical(1); return true; });
        KeyBindings.Add(Key.CursorUp, Command.Up);
        KeyBindings.Add(Key.CursorDown, Command.Down);
    }

    public void Show(List<(string Text, Tone Tone)> lines)
    {
        _lines = lines;
        _wrapWidth = -1;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Math.Max(1, Viewport.Width - 1);
        if (w != _wrapWidth)
        {
            _wrapWidth = w;
            _wrapped = _lines.SelectMany(l => Wrap(l.Text, w).Select(t => (t, l.Tone))).ToList();
            SetContentSize(new Size(w, _wrapped.Count));
        }
        var normal = GetAttributeForRole(VisualRole.Normal);
        for (var y = 0; y < Viewport.Height && y + Viewport.Y < _wrapped.Count; y++)
        {
            var (text, tone) = _wrapped[y + Viewport.Y];
            SetAttribute(tone switch
            {
                Tone.Ok => new TgAttribute(new Terminal.Gui.Drawing.Color(ColorName16.BrightGreen), normal.Background),
                Tone.Warn => new TgAttribute(new Terminal.Gui.Drawing.Color(ColorName16.BrightYellow), normal.Background),
                Tone.Fail => new TgAttribute(new Terminal.Gui.Drawing.Color(ColorName16.BrightRed), normal.Background),
                Tone.Dim => new TgAttribute(new Terminal.Gui.Drawing.Color(ColorName16.DarkGray), normal.Background),
                Tone.Title => new TgAttribute(new Terminal.Gui.Drawing.Color(ColorName16.BrightCyan), normal.Background),
                _ => normal,
            });
            AddStr(0, y, text);
        }
        return true;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (text == "") { yield return ""; yield break; }
        var sb = new StringBuilder();
        var n = 0;
        foreach (var c in text)
        {
            var cw = TextTable.Width(c.ToString());
            if (n + cw > width) { yield return sb.ToString(); sb.Clear(); n = 0; }
            sb.Append(c);
            n += cw;
        }
        yield return sb.ToString();
    }
}

/// <summary>Overview: the inheritance of disks is a collapsible tree; select a disk and press Enter or "Operation" to choose what to do.</summary>
internal sealed class OverviewPage : ListPage<OverviewData>
{
    private readonly TreeView<Tree.Node> _tree = new(new DelegateTreeBuilder<Tree.Node>(n => n.Children, n => n.Children.Count > 0)) { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(4), BorderStyle = LineStyle.Rounded, Title = "Disk" };
    private readonly Label _env = new() { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private readonly Label _selected = new() { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };

    public OverviewPage(MainView main) : base(main)
    {
        _tree.AspectGetter = n => $"{n.Disk.Name}.vhdx  [{string.Join(", ", n.Tags())}]{(n.Broken ? "  ⚠ " + n.Link : "")}";
        _tree.SelectionChanged += (_, _) => ShowSelected();
        _tree.Accepting += (_, e) => { e.Handled = true; Actions(); };
        var act = new Button { X = 0, Y = Pos.AnchorEnd(2), Text = "Actions..." };
        act.Accepting += (_, e) => { e.Handled = true; Actions(); };
        Add(_env, _tree, _selected, act, Status);
    }

    protected override OverviewData Read(Context ctx) => OverviewData.Load(ctx);

    protected override void Render(OverviewData d)
    {
        _env.Text = d.Env;
        var keep = _tree.SelectedObject?.Disk.Path;
        _tree.ClearObjects();
        _tree.AddObjects(d.Tree.Roots);
        _tree.ExpandAll();
        _tree.SelectedObject = d.Tree.All.FirstOrDefault(n => n.Disk.Path == keep) ?? d.Tree.All.FirstOrDefault()!;
        Status.Text = d.Tree.All.Count == 0 ? "No VHDX in the folder yet. Go to the 'Base disk' page and create one" : d.Totals;
        ShowSelected();
    }

    private void ShowSelected()
    {
        var n = _tree.SelectedObject;
        _selected.Text = n is null ? "" : n.Entries.Count == 0 ? "No boot entries" : "Boot entries: " + string.Join("; ", n.Entries.Select(e => $"{e.Desc} {e.FlagText}".TrimEnd()));
    }

    private void Actions()
    {
        if (_tree.SelectedObject is not { } n) return;
        var acts = DiskAction.For(n);
        if (acts.Count == 0) { MessageBox.Query(Main.App, n.Disk.Name, n.Broken ? n.Link : "No operations available for this disk", "OK"); return; }
        if (Dialogs.Pick(Main.App, n.Disk.Name + ".vhdx", acts.Select(a => a.Text).ToArray(), "") is { } text)
        {
            var a = acts.First(x => x.Text == text);
            Main.Go(a.Op, a.Prefill);
        }
    }
}

/// <summary>Shared small dialogs.</summary>
internal static class Dialogs
{
    /// <summary>Label by default eats _ as a hotkey marker, which must not happen for text like paths and administrators_authorized_keys.</summary>
    public static readonly System.Text.Rune NoHotKey = (System.Text.Rune)0xFFFF;

    public static string? Pick(IApplication app, string title, string[] choices, string current)
    {
        if (choices.Length == 0) { MessageBox.Query(app, title, "No choices; type it in the box", "OK"); return null; }
        string? picked = null;
        var dlg = new Dialog { Title = title, Width = Dim.Percent(70), Height = Math.Min(choices.Length + 5, 25) };
        var lv = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        lv.SetSource(new ObservableCollection<string>(choices));
        var idx = Array.IndexOf(choices, current);
        lv.SelectedItem = idx >= 0 ? idx : 0;
        lv.Accepting += (_, e) => { e.Handled = true; picked = choices[Math.Max(0, lv.SelectedItem ?? 0)]; app.RequestStop(dlg); };
        var ok = new Button { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1), Text = "OK", IsDefault = true };
        ok.Accepting += (_, e) => { e.Handled = true; picked = choices[Math.Max(0, lv.SelectedItem ?? 0)]; app.RequestStop(dlg); };
        var cancel = new Button { X = Pos.Center() + 4, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        cancel.Accepting += (_, e) => { e.Handled = true; app.RequestStop(dlg); };
        dlg.Add(lv, ok, cancel);
        app.Run(dlg);
        dlg.Dispose();
        return picked;
    }

    public static string? Input(IApplication app, string title, string label, string value)
    {
        string? result = null;
        var dlg = new Dialog { Title = title, Width = Dim.Percent(70), Height = 8 };
        var tf = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Text = value };
        var ok = new Button { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1), Text = "OK", IsDefault = true };
        ok.Accepting += (_, e) => { e.Handled = true; result = tf.Text; app.RequestStop(dlg); };
        var cancel = new Button { X = Pos.Center() + 4, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        cancel.Accepting += (_, e) => { e.Handled = true; app.RequestStop(dlg); };
        dlg.Add(new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = 0, Text = label }, tf, ok, cancel);
        app.Run(dlg);
        dlg.Dispose();
        return result;
    }
}

/// <summary>Boot entry management: list + buttons. WLB entries first (full-featured), non-VHD system entries after. Rename pops up a dialog, delete asks for confirmation, the rest act directly on press, and the result is shown at the bottom.</summary>
internal sealed class EntriesPage : ListPage<EntryGroups>
{
    private readonly Label _head = new() { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = 0, Width = Dim.Fill(), Text = "" };
    private readonly ListView _lv = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Percent(60), BorderStyle = LineStyle.Rounded };
    private readonly TextView _other;
    private readonly List<(EntryAction A, Button B)> _buttons = [];
    private readonly Latest _act;
    private List<Bcd.Entry> _rows = [];
    private string _mine = "";   // Job started from this page

    public EntriesPage(MainView main) : base(main)
    {
        _act = new Latest(main.Post);
        var by = Pos.Bottom(_lv) + 1;
        Pos x = 0;
        foreach (var a in EntryAction.All)
        {
            var b = new Button { X = x, Y = by, Text = a.Text };
            b.Accepting += (_, e) => { e.Handled = true; Act(a); };
            _buttons.Add((a, b));
            x = Pos.Right(b) + 1;
        }
        _lv.ValueChanged += (_, _) => Sync();
        _other = new TextView { X = 0, Y = Pos.Bottom(_buttons[0].B) + 1, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true, WordWrap = false };
        Add(_head, _lv);
        foreach (var (_, b) in _buttons) Add(b);
        Add(_other, Status);
    }

    protected override EntryGroups Read(Context ctx) => EntryGroups.Load(ctx);

    public override void JobChanged(Job job)
    {
        var result = job.Id == _mine && !job.Running;
        if (result) _mine = "";
        base.JobChanged(job);
        if (result) _message = job.State == "done" ? $"{job.Op.Title}: Done" : $"{job.Op.Title}: Failed: {job.Error}";
    }

    private string _message = "";

    private Bcd.Entry? Selected() => _lv.SelectedItem is int i and >= 0 && i < _rows.Count ? _rows[i] : null;

    /// <summary>Buttons that do not apply to the selected boot entry are greyed out.</summary>
    private void Sync()
    {
        var e = Selected();
        foreach (var (a, b) in _buttons) b.Enabled = a.Applies(e);
    }

    private void Act(EntryAction a)
    {
        var e = Selected();
        if (!a.Applies(e)) return;
        var v = a.Prefill(e);
        switch (a.Kind)
        {
            case EntryActionKind.Page:
                Main.Go(a.Op, v);
                return;
            case EntryActionKind.Rename:
                if (Dialogs.Input(Main.App, "Rename boot entry", "New name", e!.Desc) is not { } desc || desc == e.Desc) return;
                v["Desc"] = desc;
                break;
            case EntryActionKind.Confirm:
                if (MessageBox.Query(Main.App, "Delete boot entry", a.ConfirmText(e!), "Delete", "Cancel") != 0) return;
                break;
        }
        var op = Operations.Find(a.Op)!;
        Status.Text = $"{a.Text}……";
        _act.Run(() => Context.Query(Main.Cfg, ctx => op.Prepare(ctx, v)), p =>
        {
            if (p.Checks.Fails > 0)
            {
                Status.Text = "";
                MessageBox.ErrorQuery(Main.App, $"Cannot perform '{a.Text}'", string.Join("\n", p.Checks.Problems.Where(c => c.State == CoreCheckState.Fail).Select(c => $"{c.Item}: {c.Detail}")), "OK");
                return;
            }
            try { _mine = Jobs.Start(Main.Cfg, op, p.Values).Id; }
            catch (WlbError ex) { Status.Text = ""; MessageBox.ErrorQuery(Main.App, $"Cannot perform '{a.Text}'", ex.Message, "OK"); }
        }, ex => { Status.Text = ""; MessageBox.ErrorQuery(Main.App, $"Cannot perform '{a.Text}'", ex.Message, "OK"); });
    }

    protected override void Render(EntryGroups g)
    {
        var keep = Selected()?.Id;
        _rows = [.. g.Wlb, .. g.System];
        _head.Text = $"Boot entries starting with {Config.EntryPrefix} (full control): {g.Wlb.Count}; non-VHD system boot entries (default / next boot only): {g.System.Count}";
        _lv.SetSource(new ObservableCollection<string>(_rows.Select(e =>
        {
            var target = e.IsVhd ? Path.GetFileName(e.Target) : e.Target;
            return $"{(e.Tier == "system" ? "[System] " : "")}{e.Desc}  →  {target}  {e.FlagText}".TrimEnd();
        })));
        var i = _rows.FindIndex(e => e.Id == keep);
        _lv.SelectedItem = i >= 0 ? i : _rows.Count > 0 ? 0 : null;
        _other.Text = g.Other.Count == 0 ? "" : "Other VHD boot entries (not managed, listed only):\n" + string.Join("\n", g.Other.Select(e => $"  {e.Id}  {e.Desc}  {e.Target}  {e.FlagText}".TrimEnd()));
        if (_message != "") { Status.Text = _message; _message = ""; }
        Sync();
    }
}

/// <summary>
/// Draws a form from the field table, shared by operation pages and the settings page. When the structure of the field table is unchanged, only values and descriptions are updated and controls are not rebuilt, so focus and text being typed are kept; only when the structure changes is everything redrawn, with focus staying on the same key.
/// Problems found by checks are marked on the matching preset item: a marker before the name, a color change, and the description line replaced by the problem.
/// When the user changes a value, raises Changed(key, value, whether to recompute the field table now): switches, picking from a list, and input boxes on losing focus recompute immediately, while the page waits a moment while typing before recomputing.
/// </summary>
internal sealed class TuiForm : View
{
    private sealed class Row
    {
        public Field F = null!;
        public Label Name = null!;
        public View Ctl = null!;
        public Label? Help;
    }

    private readonly MainView _main;
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);   // Typed into, not yet left
    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);
    private string _shape = "";
    private bool _building;

    public event Action<string, string, bool>? Changed;

    public TuiForm(MainView main)
    {
        _main = main;
        CanFocus = true;
        VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;
    }

    private void Raise(string key, string val, bool rebuild)
    {
        if (!_building) Changed?.Invoke(key, val, rebuild);
    }

    public void Show(IReadOnlyList<Field> fields, Values values)
    {
        var shape = string.Join("|", fields.Select(f => $"{f.Key}:{f.Kind}:{f.Group}:{f.Help != ""}"));
        _building = true;
        try
        {
            if (shape == _shape) foreach (var f in fields) Update(_rows[f.Key], f, values.Get(f.Key));
            else Rebuild(fields, values);
            _shape = shape;
        }
        finally { _building = false; }
    }

    public void ShowProblems(CheckList checks)
    {
        foreach (var r in _rows.Values)
        {
            var p = checks.ProblemOf(r.F.Key);
            r.Name.Text = (p is null ? "" : p.State == CoreCheckState.Fail ? "✗ " : "! ") + Fit(r.F.Label, p is null ? 22 : 20);
            r.Name.SchemeName = p is { State: CoreCheckState.Fail } ? "Error" : null;
            if (r.Help is not null)
            {
                r.Help.Text = Trunc(p is null ? r.F.Help : $"{p.Item}: {p.Detail}", 56);
                r.Help.SchemeName = p is { State: CoreCheckState.Fail } ? "Error" : "Dialog";
            }
        }
    }

    private void Update(Row r, Field f, string val)
    {
        r.F = f;
        switch (r.Ctl)
        {
            case CheckBox cb:
                if ((cb.Value == TgCheckState.Checked) != val.IsYes) { cb.Value = val.IsYes ? TgCheckState.Checked : TgCheckState.UnChecked; }
                cb.Text = val.IsYes ? "Yes" : "No";
                break;
            case TextField tf:
                if (!tf.HasFocus && !_dirty.Contains(f.Key) && tf.Text != val) tf.Text = val;
                break;
            case Label note:
                note.Text = Trunc(f.Default, 56);
                break;
        }
    }

    private void Rebuild(IReadOnlyList<Field> fields, Values values)
    {
        var focusKey = SubViews.FirstOrDefault(v => v.HasFocus)?.Data as string;
        foreach (var v in SubViews.ToArray()) { Remove(v); v.Dispose(); }
        _dirty.Clear();
        _rows.Clear();
        var row = 0;
        string group = "";
        View? first = null, focus = null;
        foreach (var f in fields)
        {
            if (f.Group != group)
            {
                group = f.Group;
                Add(new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = row, Text = $"—— {group}", SchemeName = "Accent" });
                row++;
            }
            var r = new Row { F = f, Name = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 1, Y = row, Width = 22, Height = 1, Text = Fit(f.Label, 22) } };
            _rows[f.Key] = r;
            Add(r.Name);
            var ctl = r.Ctl = Make(f, values.Get(f.Key), row);
            Add(ctl);
            if (ctl.CanFocus)
            {
                first ??= ctl;
                if ((ctl.Data as string) == f.Key && f.Key == focusKey) focus = ctl;
                var y = row;
                ctl.HasFocusChanged += (_, _) => { if (ctl.HasFocus) EnsureVisible(y); };
            }
            if (f.Help != "" && f.Kind != FieldKind.Note)
            {
                // Takes only one line: a Label wraps automatically by content and grows taller, covering the next line
                row++;
                r.Help = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 24, Y = row, Width = Dim.Fill(1), Height = 1, Text = Trunc(f.Help, 56), SchemeName = "Dialog" };
                Add(r.Help);
            }
            row++;
        }
        SetContentSize(new Size(Viewport.Width > 0 ? Viewport.Width : 60, row + 1));
        // Restore focus only if the form had focus to begin with; otherwise focus stays on the navigation so the arrow keys keep switching pages
        if (focusKey is not null) (focus ?? first)?.SetFocus();
    }

    private View Make(Field f, string val, int row)
    {
        var key = f.Key;
        switch (f.Kind)
        {
            case FieldKind.Note:
                return new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 24, Y = row, Width = Dim.Fill(1), Height = 1, Text = Trunc(f.Default, 56) };
            case FieldKind.Bool:
            {
                var cb = new CheckBox { X = 24, Y = row, Text = val.IsYes ? "Yes" : "No", Data = key, Value = val.IsYes ? TgCheckState.Checked : TgCheckState.UnChecked };
                cb.ValueChanged += (_, _) => Raise(key, cb.Value == TgCheckState.Checked ? "Y" : "N", true);   // A boolean item affects other fields (extra presets appear / disappear)
                return cb;
            }
            case FieldKind.Choice:
            {
                var tf = TextBox(key, val, row, Dim.Fill(4), secret: false);
                tf.ReadOnly = f.Strict;
                var pick = new Button { X = Pos.AnchorEnd(3), Y = row, Text = "…", NoPadding = true, NoDecorations = true, Data = key + "#pick" };
                pick.Accepting += (_, e) =>
                {
                    e.Handled = true;
                    if (Dialogs.Pick(_main.App, _rows[key].F.Label, _rows[key].F.Choices, tf.Text) is { } sel) { tf.Text = sel; Raise(key, sel, true); }
                };
                Add(pick);
                return tf;
            }
            default:
                return TextBox(key, val, row, Dim.Fill(1), secret: f.Kind == FieldKind.Password);
        }
    }

    private TextField TextBox(string key, string val, int row, Dim width, bool secret)
    {
        var tf = new TextField { X = 24, Y = row, Width = width, Text = val, Data = key, Secret = secret };
        tf.TextChanged += (_, _) => { if (_building) return; _dirty.Add(key); Raise(key, tf.Text, false); };
        tf.HasFocusChanged += (_, _) => { if (!tf.HasFocus && _dirty.Remove(key)) Raise(key, tf.Text, true); };
        return tf;
    }

    private void EnsureVisible(int row)
    {
        var vp = Viewport;
        if (row < vp.Y) Viewport = vp with { Y = Math.Max(0, row - 1) };
        else if (row >= vp.Y + vp.Height) Viewport = vp with { Y = row - vp.Height + 2 };
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Fit(string s, int width)
    {
        if (TextTable.Width(s) <= width) return s;
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (TextTable.Width(sb.ToString() + c) > width - 1) break;
            sb.Append(c);
        }
        return sb + "…";
    }
}

/// <summary>
/// Operation page: the form is on the left; the right side shows check results before running (checked as you edit, no button to press, and problems are also marked on the form) and is replaced by step bar + progress bar + detailed log while running.
/// Computing defaults and checking both run in the background (only the last request counts) so the UI does not stall; execution is handed to Jobs, and events are sent back to the UI in batches.
/// </summary>
internal sealed class OpPage : TuiPage
{
    private readonly MainView _main;
    private readonly Operation _op;
    private readonly Values _values = new();        // Values the user has changed
    private readonly TuiForm _form;
    private readonly LinesView _side = new();
    private readonly FrameView _sideFrame;
    private readonly ProgressBar _bar;
    private readonly TextView _log;
    private readonly Label _status;
    private readonly Button _run, _back;
    private readonly Latest _refreshQ, _startQ, _vhdQ;
    private IReadOnlyList<Field> _fields = [];
    private Values _resolved = new();
    private CheckList? _last;
    private Job? _job;
    private JobView? _view;
    private bool _live;          // The follow-up job is still running
    private bool _showRun;       // The right side shows execution status
    private LogTail? _tail;      // Run in another process: read its log file
    private readonly ConcurrentQueue<BufferedOutput.Item> _pending = new();
    private long _lastSeq;
    private int _typing;
    private string _statusText = "";

    public string Lock => _op.Lock;
    /// <summary>Where the follow-up job has got to now (shown in the title row).</summary>
    public string? Headline => _live ? _view?.Headline : null;

    public OpPage(MainView main, Operation op)
    {
        _main = main;
        _op = op;
        _refreshQ = new Latest(main.Post);
        _startQ = new Latest(main.Post);
        _vhdQ = new Latest(main.Post);
        var title = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = 0, Text = $"{op.Title}  —— {op.Summary}" };
        _form = new TuiForm(main) { X = 0, Y = 2, Width = Dim.Percent(55), Height = Dim.Fill(3), BorderStyle = LineStyle.Rounded, Title = "Preset" };
        _sideFrame = new FrameView { X = Pos.Right(_form) + 1, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(3), Title = "Check" };
        _side.Width = Dim.Fill();
        _side.Height = Dim.Fill();
        _sideFrame.Add(_side);
        _bar = new ProgressBar { X = Pos.Right(_form) + 1, Y = Pos.Bottom(_sideFrame), Width = Dim.Fill(), Height = 1, Visible = false, ProgressBarFormat = ProgressBarFormat.SimplePlusPercentage };
        _log = new TextView { X = Pos.Right(_form) + 1, Y = Pos.Bottom(_bar), Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true, Visible = false, BorderStyle = LineStyle.Rounded, Title = "Detailed log" };
        _run = new Button { X = 0, Y = Pos.AnchorEnd(2), Text = "Run", IsDefault = true, Enabled = false };
        var preset = new Button { X = Pos.Right(_run) + 1, Y = Pos.AnchorEnd(2), Text = "Preset..." };
        _back = new Button { X = Pos.Right(preset) + 1, Y = Pos.AnchorEnd(2), Text = "Back to checks", Visible = false };
        _status = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "" };
        _run.Accepting += (_, e) => { e.Handled = true; RunOp(); };
        preset.Accepting += (_, e) => { e.Handled = true; PresetMenu(); };
        _back.Accepting += (_, e) => { e.Handled = true; ShowRun(false); };
        Add(title, _form, _sideFrame, _bar, _log, _run, preset, _back, _status);
        _form.Changed += (k, v, now) =>
        {
            _values[k] = v;
            var my = ++_typing;
            if (now) { Refresh(); return; }
            main.App.AddTimeout(TimeSpan.FromMilliseconds(400), () => { if (my == _typing) Refresh(); return false; });
        };
        UpdateButtons();
    }

    public void Prefill(Values v)
    {
        _values.Clear();
        foreach (var (k, val) in v) _values[k] = val;
        Refresh();
    }

    /// <summary>Re-list every time this page is switched to (base disks and child disks may have changed); values already filled in are left alone.</summary>
    public override void Shown() => Refresh();

    public override void JobChanged(Job job)
    {
        if (_live && job.Id == _job?.Id && !job.Running)
        {
            _live = false;
            Flush();
            if (_job.Local) _job.Out.Added -= OnAdded;
            _view?.Finish(job.State, job.Error);
            _job = job;
            RenderSide();
            SetStatus(job.State == "done" ? $"{_op.Title} done" : $"Error: {job.Error}");
            Refresh();   // Disks and boot entries changed, so the defaults change with them
        }
        UpdateButtons();
    }

    /// <summary>If a job is running for the same menu item (lock group), it cannot run; if the running one is exactly this operation (including ones started by another program or another person), attach to its progress. It also cannot run when the checks have not passed.</summary>
    private void UpdateButtons()
    {
        var cur = Jobs.RunningFor(_op);
        _run.Enabled = cur is null && _last is { Passed: true };
        if (cur is null || cur.Id == _job?.Id) return;
        if (cur.Op == _op) Attach(cur);
        else SetStatus($"'{cur.Op.Title}' is running{(cur.Local ? "" : $" on {cur.Who}")}; wait for it to finish before running this one");
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        ShowStatus();
    }

    private void ShowStatus() => _status.Text = (_refreshQ.Busy || _startQ.Busy ? "… " : "") + _statusText;

    private Values Merge()
    {
        var v = _resolved.Clone();
        foreach (var (k, val) in _values) v[k] = val;
        return v;
    }

    private void ApplyPrepared(Operation.Prepared p)
    {
        (_resolved, _fields, _last) = (p.Values, p.Fields, p.Checks);
        _form.Show(_fields, Merge());
        _form.ShowProblems(p.Checks);
        if (!_showRun) RenderSide();
        UpdateButtons();
    }

    public override void Refresh()
    {
        var v = _values.Clone();
        _refreshQ.Run(() => Context.Query(_main.Cfg, ctx => _op.Prepare(ctx, v)),
            p => { ApplyPrepared(p); ShowStatus(); },
            e => SetStatus($"Error: {e.Message}"));
        ShowStatus();
    }

    private void RunOp()
    {
        if (Jobs.RunningFor(_op) is { } r) { MessageBox.Query(_main.App, "Running", $"'{r.Op.Title}' is running{(r.Local ? "" : $" on {r.Who}")}; wait for it to finish. Other menu items can run at the same time", "OK"); return; }
        // Check again at the moment of the press: the check result may be stale
        var v = _values.Clone();
        _startQ.Run(() => Context.Query(_main.Cfg, ctx => _op.Prepare(ctx, v)), p =>
        {
            ApplyPrepared(p);
            ShowStatus();
            if (p.Checks.Fails > 0) return;
            // MessageBox selects the last button by default: here Enter starts the run, whereas for delete Enter cancels
            if (_op.Mutates && MessageBox.Query(_main.App, $"Start {_op.Title}?", ConfirmText(p.Checks), "Cancel", "Start") != 1) return;
            try { Attach(Jobs.Start(_main.Cfg, _op, p.Values)); }
            catch (WlbError e) { MessageBox.ErrorQuery(_main.App, "Run", e.Message, "OK"); }
        }, e => SetStatus($"Error: {e.Message}"));
        ShowStatus();
    }

    private string ConfirmText(CheckList checks)
    {
        List<string> l = [.. _op.Stages.Select((s, i) => $"{i + 1}. {s}")];
        l.AddRange(checks.Problems.Select(c => $"! {c.Item}: {c.Detail}"));
        return l.Count > 0 ? string.Join("\n", l) : _op.Summary;
    }

    private void ShowRun(bool run)
    {
        _showRun = run;
        _sideFrame.Title = run ? "Progress" : "Check";
        _sideFrame.Height = run ? Dim.Percent(55) : Dim.Fill(3);
        _log.Visible = run;
        _back.Visible = run && !_live;
        if (!run) _bar.Visible = false;
        RenderSide();
    }

    private void RenderSide()
    {
        if (_showRun && _view is { } v && _job is { } j) _side.Show(RunLines(v, j));
        else if (_last is { } c) _side.Show(CheckLines(c));
    }

    private List<(string, Tone)> CheckLines(CheckList c)
    {
        List<(string, Tone)> l =
        [
            c.Fails > 0 ? ($"✗ {c.Fails} problem(s) to fix", Tone.Fail) : c.Warns > 0 ? ($"! Ready to run, {c.Warns} warning(s)", Tone.Warn) : ("✓ Ready to run", Tone.Ok),
            ("", Tone.Normal),
        ];
        foreach (var p in c.Problems)
        {
            l.Add(($"{(p.State == CoreCheckState.Fail ? "✗" : "!")} {p.Item}", p.State == CoreCheckState.Fail ? Tone.Fail : Tone.Warn));
            if (p.Detail != "") l.Add(($"  {p.Detail}", Tone.Normal));
        }
        var fine = c.Fine.ToList();
        if (fine.Count > 0)
        {
            l.Add(("", Tone.Normal));
            l.Add(($"{fine.Count} passed", Tone.Title));
            foreach (var f in fine) l.Add(($"{(f.State == CoreCheckState.Skip ? "-" : "✓")} {f.Item}{(f.Detail != "" ? ": " + f.Detail : "")}", Tone.Dim));
        }
        if (c.Extra is { } t)
        {
            l.Add(("", Tone.Normal));
            foreach (var row in TextTable.Format(t.Headers, t.Rows)) l.Add((row, Tone.Normal));
        }
        if (_op.Steps.Length > 0)
        {
            l.Add(("", Tone.Normal));
            l.Add(("What will happen", Tone.Title));
            for (var i = 0; i < _op.Steps.Length; i++) l.Add(($"{i + 1}. {_op.Steps[i]}", Tone.Dim));
        }
        return l;
    }

    private static List<(string, Tone)> RunLines(JobView v, Job job)
    {
        var running = v.State == "running";
        var took = (running ? DateTime.Now : job.EndedAt ?? DateTime.Now) - job.StartedAt;
        List<(string, Tone)> l =
        [
            running ? ($"Running{(job.Local ? "" : $" ({job.Who})")}, elapsed {took:m\\:ss}", Tone.Title)
                : v.State == "done" ? ($"✓ {job.Op.Title} done, took {took:m\\:ss}", Tone.Ok) : ($"✗ {job.Op.Title} failed: {v.Error}", Tone.Fail),
            ("", Tone.Normal),
        ];
        foreach (var s in v.Steps)
        {
            l.Add(s.State switch
            {
                StepState.Running => ($"▶ {s.Label}{(v.Percent >= 0 ? $" {v.Percent}%" : "")}", Tone.Title),
                StepState.Done => ($"✓ {s.Label}", Tone.Ok),
                StepState.Failed => ($"✗ {s.Label}", Tone.Fail),
                StepState.Skipped => ($"- {s.Label}", Tone.Dim),
                _ => ($"  {s.Label}", Tone.Dim),
            });
            if (s.Detail != "" && s.State != StepState.Pending) l.Add(($"    {s.Detail}", Tone.Dim));
            if (s.State == StepState.Running && v.Activity != "") l.Add(($"    {v.Activity}", Tone.Normal));
        }
        if (v.Steps.Count == 0 && running && v.Activity != "") l.Add((v.Activity, Tone.Normal));
        if (v.Warnings.Count > 0)
        {
            l.Add(("", Tone.Normal));
            foreach (var w in v.Warnings) l.Add(($"! {w}", Tone.Warn));
        }
        if (v.Results.Count > 0)
        {
            l.Add(("", Tone.Normal));
            foreach (var (k, val) in v.Results) l.Add(($"{TextTable.Pad(k, 10)} {val}", Tone.Normal));
        }
        return l;
    }

    private void Attach(Job job)
    {
        if (_live && _job is { Local: true } old) old.Out.Added -= OnAdded;
        _job = job;
        _live = job.Running;
        _view = new JobView(job.Op);
        _lastSeq = 0;
        _tail = null;
        _log.Text = "";
        if (job.Local)
        {
            job.Out.Added += OnAdded;   // Subscribe first, then replay, so nothing produced in between is missed; Flush deduplicates by sequence number
            foreach (var it in job.Out.Since(0)) _pending.Enqueue(it);
        }
        else
        {
            _tail = new LogTail(job.LogPath);
            _log.Text = $"(running in {job.Who}, process {job.Pid}; its log is below: {job.LogPath})";
        }
        SetStatus("");
        _run.Enabled = false;
        ShowRun(true);
        _main.App.AddTimeout(TimeSpan.FromMilliseconds(150), () => { Flush(); return _live || !_pending.IsEmpty; });
    }

    /// <summary>Called on the execution thread: only enqueues, the UI thread takes items on a timer.</summary>
    private void OnAdded(BufferedOutput.Item it) => _pending.Enqueue(it);

    private void Flush()
    {
        if (_view is not { } view) return;
        var sb = new StringBuilder();
        void Add(string l) { if (_log.Text.Length + sb.Length > 0) sb.Append('\n'); sb.Append(l); }
        if (_tail is not null)
            foreach (var l in _tail.ReadNew()) { view.ApplyLog(l); Add(l); }
        while (_pending.TryDequeue(out var it))
        {
            if (it.Kind != "progress" && it.Seq <= _lastSeq) continue;
            _lastSeq = Math.Max(_lastSeq, it.Seq);
            if (view.Apply(it) is { } line) Add(line);
        }
        if (sb.Length > 0)
        {
            _log.Text += sb.ToString();
            _log.MoveEnd();
        }
        _bar.Visible = _showRun && _live;
        if (_live)
        {
            _bar.ProgressBarStyle = view.Percent >= 0 ? ProgressBarStyle.Blocks : ProgressBarStyle.MarqueeBlocks;
            if (view.Percent >= 0) _bar.Fraction = view.Percent / 100f;
            else _bar.Pulse();
        }
        _back.Visible = _showRun && !_live;
        if (_showRun) RenderSide();
    }

    private void PresetMenu()
    {
        List<string> items = ["Import preset", "Export preset", "Restore defaults"];
        if (_op is Ops.LayerOp) items.Insert(1, "Import from VHD");
        switch (Dialogs.Pick(_main.App, "Preset", items.ToArray(), ""))
        {
            case "Import preset": Import(); break;
            case "Import from VHD": ImportFromVhd(); break;
            case "Export preset": Export(); break;
            case "Restore defaults": _values.Clear(); Refresh(); break;
        }
    }

    private void Import()
    {
        var dlg = new OpenDialog { Title = "Import preset (JSON)", AllowedTypes = [new AllowedType("Preset file", ".json")] };
        _main.App.Run(dlg);
        var path = dlg.Canceled ? null : dlg.FilePaths.FirstOrDefault() ?? dlg.Path;
        dlg.Dispose();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Prefill(_op.FromPreset(Preset.Load(path)));
            SetStatus($"Imported {path}");
        }
        catch (WlbError e) { MessageBox.ErrorQuery(_main.App, "Import", e.Message, "OK"); }
    }

    private void ImportFromVhd()
    {
        void Fail(Exception e) { SetStatus(""); MessageBox.ErrorQuery(_main.App, "Import from VHD", e.Message, "OK"); }
        SetStatus("Listing child disks...");
        _vhdQ.Run(() => Context.Query(_main.Cfg, ctx => ctx.ChildDisks().Select(d => d.Name).ToArray()), names =>
        {
            SetStatus("");
            if (Dialogs.Pick(_main.App, "Import from which child disk", names, "") is not { } name) return;
            SetStatus($"Reading {name}...");
            _vhdQ.Run(() => Context.Query(_main.Cfg, ctx => ((Ops.LayerOp)_op).FromVhd(ctx, ctx.Cfg.VhdPath(name))), v =>
            {
                Prefill(v);
                SetStatus($"Imported settings from {name}");
            }, Fail);
        }, Fail);
    }

    private void Export()
    {
        var dlg = new SaveDialog { Title = "Export preset (JSON)", AllowedTypes = [new AllowedType("Preset file", ".json")] };
        dlg.Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Preset.FileName(_op.Name));
        _main.App.Run(dlg);
        var path = dlg.Canceled ? null : dlg.Path;
        dlg.Dispose();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _op.ToPreset(Merge(), _fields, "wlb tui").Save(path);
            SetStatus($"Exported to {path}");
        }
        catch (Exception e) { MessageBox.ErrorQuery(_main.App, "Export", e.Message, "OK"); }
    }
}
