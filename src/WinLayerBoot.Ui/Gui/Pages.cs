using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinLayerBoot.Core;

namespace WinLayerBoot.Gui;

/// <summary>Pages are resident (not destroyed when switched away); the window notifies them when switched to and when a job starts / ends (all on the UI thread).</summary>
internal interface IPage
{
    void Shown();
    void JobChanged(Job job);
}

internal static class Ui
{
    // Glyphs of Segoe Fluent Icons
    public const string GlyphOk = "", GlyphError = "", GlyphWarn = "", GlyphSkip = "", GlyphPending = "", GlyphCopy = "",
        GlyphImport = "", GlyphExport = "", GlyphUndo = "", GlyphDisk = "", GlyphBase = "";

    /// <summary>Small tags (status of boot entries and disks).</summary>
    public static Border Pill(string s, Color? tint = null) => new()
    {
        Child = new TextBlock { Text = s, FontSize = 12 },
        Padding = new Thickness(8, 1, 8, 2), CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(tint ?? Colors.Gray) { Opacity = tint is null ? 0.18 : 0.3 },
    };

    public static InfoBar Bar(InfoBarSeverity severity, string title, string message = "") => new() { IsOpen = true, IsClosable = false, Severity = severity, Title = title, Message = message };

    private static bool _dialogOpen;

    /// <summary>Shows a dialog. XAML allows only one open at a time and throws if another is opened: when one is already open, the second is not shown and counts as cancelled.</summary>
    public static async System.Threading.Tasks.Task<ContentDialogResult> Show(ContentDialog d)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try { return await d.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    public static TextBlock Text(string s, double size = 14, bool bold = false, bool dim = false) => new()
    {
        Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Opacity = dim ? 0.7 : 1,
    };

    public static TextBlock Title(string s) => Text(s, 24, true);

    public static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        // Do not take theme brushes from the resource dictionary: casting fails with "No such interface supported" in single-file publish
        BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.35 },
        Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.08 },
    };

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var st) && st is Style style) b.Style = style;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Brush StateBrush(string state) => new SolidColorBrush(state switch
    {
        "OK" => Colors.SeaGreen, "Warning" => Colors.DarkOrange, "Failed" => Colors.IndianRed, _ => Colors.Gray,
    });

    public static async void Error(XamlRoot root, string title, string message)
    {
        var d = new ContentDialog { Title = title, Content = Text(message), CloseButtonText = "OK", XamlRoot = root };
        await Ui.Show(d);
    }

    /// <summary>Page header: title, refresh button, spinner, status text.</summary>
    public static StackPanel Head(string title, Action refresh, ProgressRing ring, TextBlock status)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Title(title));
        var b = Button("Refresh", refresh);
        b.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(b);
        ring.Width = ring.Height = 18;
        ring.VerticalAlignment = status.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(ring);
        head.Children.Add(status);
        return head;
    }
}

/// <summary>Common part of list pages (overview, boot entries): reads data in the background (only the last request counts); after a job ends the data is stale, and it is re-read immediately if shown, or when switched to if not shown.</summary>
internal abstract partial class ListPage<T> : Grid, IPage
{
    protected readonly MainWindow Win;
    protected readonly ProgressRing Ring = new();
    protected readonly TextBlock Status = Ui.Text("", dim: true);
    private readonly Latest _q;

    protected ListPage(MainWindow win)
    {
        Win = win;
        _q = new Latest(win.Post);
    }

    protected abstract T Read(Context ctx);
    protected abstract void Render(T data);

    public void Shown() => Load();

    public virtual void JobChanged(Job job)
    {
        if (job.Running) return;
        if (Win.IsShown(this)) Load();
    }

    protected void Load()
    {
        Ring.IsActive = true;
        Status.Text = "Loading...";
        _q.Run(() => Context.Query(Win.Cfg, Read),
            d => { Ring.IsActive = false; Status.Text = $"Loaded {DateTime.Now:HH:mm:ss}"; Render(d); },
            e => { Ring.IsActive = false; Status.Text = $"Failed to load: {e.Message}"; });
    }
}

/// <summary>Overview: the inheritance tree (one card per disk, indented by level).</summary>
internal sealed partial class OverviewPage : ListPage<OverviewData>
{
    private readonly StackPanel _panel = new();

    public OverviewPage(MainWindow win) : base(win)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(Ui.Head("Overview", Load, Ring, Status));
        var scroll = new ScrollViewer { Content = _panel };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
    }

    protected override OverviewData Read(Context ctx) => OverviewData.Load(ctx);

    protected override void Render(OverviewData d)
    {
        _panel.Children.Clear();
        var t = d.Tree;
        if (!d.IsAdmin) _panel.Children.Add(Ui.Bar(InfoBarSeverity.Error, "Not administrator", "You can view, but operations will fail. Reopen as administrator."));
        if (!d.OnWorkHost) _panel.Children.Add(Ui.Bar(InfoBarSeverity.Warning, "Not on the work host", "Creating child disks, taking snapshots, exporting and adding boot entries must run in Server."));
        if (t.All.Any(x => x.Broken)) _panel.Children.Add(Ui.Bar(InfoBarSeverity.Warning, $"{t.All.Count(x => x.Broken)} disks have problems", "See the cards marked red below."));
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };
        stats.Children.Add(Stat("Disks", t.All.Count.ToString()));
        stats.Children.Add(Stat("Base disk", t.All.Count(x => x.Disk.IsBase).ToString()));
        stats.Children.Add(Stat("WLB boot entries", t.All.Sum(x => x.Entries.Count).ToString()));
        stats.Children.Add(Stat($"{d.VhdDir} free", $"{d.FreeGB} GB"));
        _panel.Children.Add(stats);
        if (t.All.Count == 0)
        {
            var empty = new StackPanel { Spacing = 8 };
            empty.Children.Add(Ui.Text("No VHDX in the folder yet", 15, true));
            empty.Children.Add(Ui.Text("The first step is to create a base disk from the install ISO.", dim: true));
            var go = Ui.Button("Create a base disk", () => Win.Go("base"), accent: true);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            empty.Children.Add(go);
            _panel.Children.Add(Ui.Card(empty));
        }
        foreach (var r in t.Roots) AddNode(r);
        if (t.Orphans.Count > 0)
        {
            var sp = new StackPanel { Spacing = 2 };
            foreach (var e in t.Orphans) sp.Children.Add(Ui.Text($"{e.Desc}  → {e.Target}  {e.FlagText}", 12, dim: true));
            _panel.Children.Add(new Expander { Header = $"{t.Orphans.Count} boot entries point to missing files or files outside the folder", Content = sp, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left });
        }
    }

    private static Border Stat(string label, string value)
    {
        var sp = new StackPanel { MinWidth = 84 };
        sp.Children.Add(Ui.Text(value, 20, true));
        sp.Children.Add(Ui.Text(label, 12, dim: true));
        var c = Ui.Card(sp);
        c.Margin = new Thickness(0);
        return c;
    }

    private void AddNode(Tree.Node n)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new FontIcon { Glyph = n.Disk.IsBase ? Ui.GlyphBase : Ui.GlyphDisk, FontSize = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(Ui.Text(n.Disk.Name + ".vhdx", 15, true));
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var t in n.Tags()) tags.Children.Add(Ui.Pill(t));
        sp.Children.Add(tags);
        if (n.Broken) sp.Children.Add(new TextBlock { Text = n.Link, TextWrapping = TextWrapping.Wrap, Foreground = Ui.StateBrush("Failed") });
        foreach (var e in n.Entries) sp.Children.Add(Ui.Text($"Boot entry: {e.Desc}  {e.FlagText}".TrimEnd(), 12, dim: true));
        if (n.Entries.Count == 0 && !n.Disk.IsBase) sp.Children.Add(Ui.Text("No boot entries", 12, dim: true));
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        if (DiskAction.For(n) is { Count: > 0 } acts)
        {
            var more = new MenuFlyout();
            foreach (var a in acts)
            {
                var i = new MenuFlyoutItem { Text = a.Text };
                i.Click += (_, _) => Win.Go(a.Op, a.Prefill);
                more.Items.Add(i);
            }
            var b = new DropDownButton { Content = "Actions", Flyout = more, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(b, 2);
            g.Children.Add(b);
            g.ContextFlyout = more;
        }
        var card = Ui.Card(g);
        card.Margin = new Thickness(n.Depth * 28, 0, 0, 8);
        _panel.Children.Add(card);
        foreach (var c in n.Children) AddNode(c);
    }
}

/// <summary>Boot entry management: two selectable lists (WLB full-featured / system entries that can only be set as default or next boot) + toolbar and context menu. Rename pops up a dialog, delete asks for confirmation, the rest act directly on click, and the result is shown at the top.</summary>
internal sealed partial class EntriesPage : ListPage<EntryGroups>
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListView _sysList = new() { SelectionMode = ListViewSelectionMode.Single, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _sysBox = new(), _otherBox = new();
    private readonly List<(EntryAction A, AppBarButton B)> _buttons = [];
    private readonly InfoBar _result = new() { IsOpen = false, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Latest _act;
    private string _mine = "";   // Job started from this page
    private bool _syncing;

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["entry-add"] = "", ["entry-rename"] = "", ["entry-delete"] = "", ["entry-hide"] = "",
        ["entry-show"] = "", ["entry-next"] = "", ["entry-default"] = "",
    };

    public EntriesPage(MainWindow win) : base(win)
    {
        _act = new Latest(win.Post);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(Ui.Head("Boot entries", Load, Ring, Status));
        var bar = new CommandBar { HorizontalAlignment = HorizontalAlignment.Left, DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = false };
        foreach (var a in EntryAction.All)
        {
            var b = new AppBarButton { Label = a.Text, Icon = new FontIcon { Glyph = Glyphs[a.Op] } };
            b.Click += (_, _) => Act(a);
            _buttons.Add((a, b));
            bar.PrimaryCommands.Add(b);
        }
        Grid.SetRow(bar, 1);
        Children.Add(bar);
        Grid.SetRow(_result, 2);
        Children.Add(_result);
        // The two lists are mutually exclusive: selecting in one clears the other
        _list.SelectionChanged += (_, _) => Pick(_list, _sysList);
        _sysList.SelectionChanged += (_, _) => Pick(_sysList, _list);
        var body = new StackPanel();
        body.Children.Add(Ui.Text($"Boot entries starting with {Config.EntryPrefix} (full control)", bold: true));
        body.Children.Add(_list);
        _sysBox.Children.Add(Ui.Text("Non-VHD system boot entries (can only be set as default / next boot)", bold: true));
        _sysBox.Children.Add(_sysList);
        body.Children.Add(_sysBox);
        body.Children.Add(_otherBox);
        var scroll = new ScrollViewer { Content = body };
        Grid.SetRow(scroll, 3);
        Children.Add(scroll);
    }

    protected override EntryGroups Read(Context ctx) => EntryGroups.Load(ctx);

    public override void JobChanged(Job job)
    {
        if (job.Id == _mine && !job.Running)
        {
            _mine = "";
            ShowResult(job.State == "done" ? InfoBarSeverity.Success : InfoBarSeverity.Error, job.State == "done" ? $"{job.Op.Title}: Done" : $"{job.Op.Title}: Failed", job.Error);
        }
        base.JobChanged(job);
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message = "")
    {
        _result.Severity = severity;
        _result.Title = title;
        _result.Message = message;
        _result.IsOpen = true;
    }

    private Bcd.Entry? Selected() => (_list.SelectedItem ?? _sysList.SelectedItem) is ListViewItem { Tag: Bcd.Entry e } ? e : null;

    private void Pick(ListView from, ListView other)
    {
        if (_syncing || from.SelectedItem is null) return;
        _syncing = true;
        other.SelectedItem = null;
        _syncing = false;
        Sync();
    }

    /// <summary>Buttons that do not apply to the selected boot entry are greyed out.</summary>
    private void Sync()
    {
        var e = Selected();
        foreach (var (a, b) in _buttons) b.IsEnabled = a.Applies(e);
    }

    private async void Act(EntryAction a) => await Act(a, Selected());

    private async System.Threading.Tasks.Task Act(EntryAction a, Bcd.Entry? e)
    {
        if (!a.Applies(e)) return;
        var v = a.Prefill(e);
        switch (a.Kind)
        {
            case EntryActionKind.Page:
                Win.Go(a.Op, v);
                return;
            case EntryActionKind.Rename:
                var box = new TextBox { Text = e!.Desc, Header = "New name", MinWidth = 360, SelectionStart = e.Desc.Length };
                var rename = new ContentDialog { Title = "Rename boot entry", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
                if (await Ui.Show(rename) != ContentDialogResult.Primary || box.Text == e.Desc) return;
                v["Desc"] = box.Text;
                break;
            case EntryActionKind.Confirm:
                var ask = new ContentDialog { Title = "Delete boot entry", Content = Ui.Text(a.ConfirmText(e!)), PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
                if (await Ui.Show(ask) != ContentDialogResult.Primary) return;
                break;
        }
        var op = Operations.Find(a.Op)!;
        _act.Run(() => Context.Query(Win.Cfg, ctx => op.Prepare(ctx, v)), p =>
        {
            if (p.Checks.Fails > 0)
            {
                ShowResult(InfoBarSeverity.Error, $"Cannot perform '{a.Text}'", string.Join("\n", p.Checks.Problems.Where(c => c.State == CheckState.Fail).Select(c => $"{c.Item}: {c.Detail}")));
                return;
            }
            try { _mine = Jobs.Start(Win.Cfg, op, p.Values).Id; }
            catch (WlbError ex) { ShowResult(InfoBarSeverity.Error, $"Cannot perform '{a.Text}'", ex.Message); }
        }, ex => ShowResult(InfoBarSeverity.Error, $"Cannot perform '{a.Text}'", ex.Message));
    }

    /// <summary>Context menu: the same operations as the toolbar, applied to the item that was right-clicked.</summary>
    private MenuFlyout RowMenu(Bcd.Entry e)
    {
        var m = new MenuFlyout();
        foreach (var a in EntryAction.All.Where(a => a.NeedSel))
        {
            var i = new MenuFlyoutItem { Text = a.Text, Icon = new FontIcon { Glyph = Glyphs[a.Op] }, IsEnabled = a.Applies(e) };
            i.Click += async (_, _) => await Act(a, e);
            m.Items.Add(i);
        }
        return m;
    }

    protected override void Render(EntryGroups g)
    {
        var keep = Selected()?.Id;
        _syncing = true;
        try
        {
            Fill(_list, g.Wlb, keep);
            if (g.Wlb.Count == 0) _list.Items.Add(new ListViewItem { Content = Ui.Text($"No boot entries starting with {Config.EntryPrefix}", dim: true), IsEnabled = false });
            Fill(_sysList, g.System, keep);
        }
        finally { _syncing = false; }
        _sysBox.Visibility = g.System.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _otherBox.Children.Clear();
        if (g.Other.Count > 0)
        {
            var sp = new StackPanel { Spacing = 2 };
            foreach (var e in g.Other) sp.Children.Add(Ui.Text($"{e.Desc}  {e.Target}  {e.FlagText}".TrimEnd(), 12, dim: true));
            _otherBox.Children.Add(new Expander { Header = $"{g.Other.Count} other VHD boot entries (listed only, not managed)", Content = sp, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left });
        }
        Sync();
    }

    private void Fill(ListView lv, List<Bcd.Entry> entries, string? keep)
    {
        lv.Items.Clear();
        foreach (var e in entries)
        {
            var sp = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 6) };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(Ui.Text(e.Desc, 15, true));
            foreach (var f in e.Flags()) line.Children.Add(Ui.Pill(f, f is "Default" or "Next boot" ? Colors.DodgerBlue : f == "File missing" ? Colors.IndianRed : null));
            sp.Children.Add(line);
            sp.Children.Add(Ui.Text($"{e.Id}  →  {e.Target}", 12, dim: true));
            var item = new ListViewItem { Content = sp, Tag = e, ContextFlyout = RowMenu(e) };
            lv.Items.Add(item);
            if (e.Id == keep) lv.SelectedItem = item;
        }
    }
}

/// <summary>Settings: drawn from fields like the operation pages (Config.Fields); the boot menu language is a non-editable dropdown.</summary>
internal sealed partial class SettingsPage : Grid, IPage
{
    private readonly MainWindow _win;
    private readonly FieldForm _form = new();
    private readonly Values _values = new();   // Changed, not yet saved
    private readonly TextBlock _resolved = Ui.Text("", dim: true);
    private readonly TextBlock _status = Ui.Text("");

    public SettingsPage(MainWindow win)
    {
        _win = win;
        var sp = new StackPanel { Spacing = 10, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(Ui.Title("Settings"));
        sp.Children.Add(Ui.Text($"Shared settings for all four interfaces: {win.Cfg.LoadedFrom}", dim: true));
        sp.Children.Add(_form);
        sp.Children.Add(_resolved);
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(Ui.Button("Save", Save, accent: true));
        bar.Children.Add(Ui.Button("Discard changes", () => { _values.Clear(); Render(); _status.Text = ""; }));
        sp.Children.Add(bar);
        sp.Children.Add(_status);
        Children.Add(new ScrollViewer { Content = sp });
        _form.Changed += (k, v, _) => { _values[k] = v; _status.Text = "Unsaved changes"; };
        Render();
    }

    public void Shown() { if (_values.Count == 0) Render(); }   // Files in the iso directory may have changed
    public void JobChanged(Job job) { }

    private void Render()
    {
        var cfg = _win.Cfg;
        var fields = cfg.Fields();
        var v = new Values();
        foreach (var f in fields) v[f.Key] = f.Default;
        foreach (var (k, val) in _values) v[k] = val;
        _form.Show(fields, v);
        _resolved.Text = $"ISOs actually used:\n  LOF {Or(cfg.ResolveLofIso())}\n  virtio {Or(cfg.ResolveVirtioIso())}";
    }

    private void Save()
    {
        var cfg = _win.Cfg;
        try
        {
            cfg.Update(_values);   // Re-read the file under the lock and change only these items, so as not to overwrite what someone else just saved
            _values.Clear();
            Render();
            _status.Text = $"Saved to {cfg.LoadedFrom}";
        }
        catch (WlbError e) { _status.Text = e.Message; }
    }

    private static string Or(string s) => s == "" ? "(not found)" : s;
}
