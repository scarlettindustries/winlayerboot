using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinLayerBoot.Core;

namespace WinLayerBoot.Gui;

/// <summary>
/// Operation page. The form is on the left, with "Run" and "Preset" menus at the bottom; the right side shows check results before running (checked as you edit, no button to press, and problems are also shown under the input boxes) and is replaced by the step bar while running.
/// Computing defaults and checking both run in the background (only the last request counts) so the UI does not stall; execution is handed to Jobs, and events are sent back to the UI thread in batches. The page is not destroyed when switched away, so entered values and progress remain.
/// </summary>
internal sealed partial class OpPage : Grid, IPage
{
    private readonly MainWindow _win;
    private readonly Operation _op;
    private readonly Values _values = new();          // Values the user has changed
    private readonly FieldForm _form = new();
    private readonly CheckPanel _checks = new();
    private readonly RunPanel _runPanel;
    private readonly ScrollViewer _checksView, _runView;
    private readonly TextBlock _status = Ui.Text("", 12, dim: true);
    private readonly ProgressRing _ring = new() { Width = 18, Height = 18, IsActive = false, Margin = new Thickness(4, 0, 0, 0) };
    private readonly Button _runBtn;
    private readonly Latest _refreshQ, _startQ;
    private IReadOnlyList<Field> _fields = [];
    private Values _resolved = new();
    private CheckList? _last;
    private Job? _job;
    private JobView? _view;
    private bool _live;          // The follow-up job is still running
    private LogTail? _tail;      // Run in another process: read its log file
    private readonly ConcurrentQueue<BufferedOutput.Item> _pending = new();
    private readonly DispatcherQueueTimer _flush, _typing;
    private long _lastSeq;

    public string Lock => _op.Lock;
    /// <summary>Where the follow-up job has got to now (shown at the bottom of the navigation bar).</summary>
    public string? Headline => _live ? _view?.Headline : null;

    public OpPage(MainWindow win, Operation op)
    {
        _win = win;
        _op = op;
        _refreshQ = new Latest(win.Post);
        _startQ = new Latest(win.Post);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(Ui.Title(op.Title));
        head.Children.Add(Ui.Text(op.Summary, dim: true));
        Grid.SetColumnSpan(head, 2);
        Children.Add(head);

        // Left: form + action bar
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(new ScrollViewer { Content = _form, Padding = new Thickness(0, 0, 16, 0) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        _runBtn = Ui.Button("Run", RunOp, accent: true);
        _runBtn.IsEnabled = false;
        _runBtn.MinWidth = 96;
        _runBtn.Margin = new Thickness(0);
        bar.Children.Add(_runBtn);
        bar.Children.Add(new DropDownButton { Content = "Preset", Flyout = PresetMenu() });
        bar.Children.Add(_ring);
        _status.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_status);
        Grid.SetRow(bar, 1);
        left.Children.Add(bar);
        Grid.SetRow(left, 1);
        Children.Add(left);

        // Right: check results / execution status
        _runPanel = new RunPanel(() => ShowRun(false));
        _checksView = new ScrollViewer { Content = _checks, Padding = new Thickness(0, 0, 12, 0), Margin = new Thickness(16, 0, 0, 0) };
        _runView = new ScrollViewer { Content = _runPanel, Padding = new Thickness(0, 0, 12, 0), Margin = new Thickness(16, 0, 0, 0), Visibility = Visibility.Collapsed };
        foreach (var v in new[] { _checksView, _runView })
        {
            Grid.SetRow(v, 1);
            Grid.SetColumn(v, 1);
            Children.Add(v);
        }

        _checks.FieldClicked += _form.FocusField;
        _typing = win.Dq.CreateTimer();
        _typing.Interval = TimeSpan.FromMilliseconds(400);
        _typing.IsRepeating = false;
        _typing.Tick += (_, _) => Refresh();
        _form.Changed += (k, v, now) =>
        {
            _values[k] = v;
            _typing.Stop();
            if (now) Refresh();
            else _typing.Start();
        };
        _flush = win.Dq.CreateTimer();
        _flush.Interval = TimeSpan.FromMilliseconds(120);
        _flush.Tick += (_, _) => Flush();
        UpdateButtons();
    }

    private MenuFlyout PresetMenu()
    {
        var m = new MenuFlyout();
        void Add(string text, string glyph, Action act)
        {
            var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            i.Click += (_, _) => act();
            m.Items.Add(i);
        }
        Add("Import preset...", Ui.GlyphImport, Import);
        if (_op is Ops.LayerOp) Add("Import from VHD...", Ui.GlyphDisk, ImportFromVhd);
        Add("Export preset...", Ui.GlyphExport, Export);
        m.Items.Add(new MenuFlyoutSeparator());
        Add("Restore defaults", Ui.GlyphUndo, () => { _values.Clear(); Refresh(); });
        return m;
    }

    /// <summary>The window is closed: stop updating the UI.</summary>
    public void Close()
    {
        _flush.Stop();
        _typing.Stop();
    }

    /// <summary>Jumped here from another page: switch to the prefilled values.</summary>
    public void Prefill(Values v)
    {
        _values.Clear();
        foreach (var (k, val) in v) _values[k] = val;
        Refresh();
    }

    /// <summary>Re-list every time this page is switched to (base disks and child disks may have changed); values already filled in are left alone.</summary>
    public void Shown() => Refresh();

    public void JobChanged(Job job)
    {
        if (_live && job.Id == _job?.Id && !job.Running)
        {
            _live = false;
            Flush();
            if (_job.Local) _job.Out.Added -= OnAdded;
            _view?.Finish(job.State, job.Error);
            _job = job;
            RenderRun();
            Refresh();   // Disks and boot entries changed, so the defaults change with them
        }
        UpdateButtons();
    }

    /// <summary>If a job is running for the same menu item (lock group), it cannot run; if the running one is exactly this operation (including ones started by another program or another person), attach to its progress. It also cannot run when the checks have not passed.</summary>
    private void UpdateButtons()
    {
        var cur = Jobs.RunningFor(_op);
        _runBtn.IsEnabled = cur is null && _last is { Passed: true };
        if (cur is null || cur.Id == _job?.Id)
        {
            if (cur is null && !_refreshQ.Busy) _status.Text = "";
            return;
        }
        if (cur.Op == _op) Attach(cur);
        else _status.Text = $"'{cur.Op.Title}' is running{(cur.Local ? "" : $" in {cur.Who}")}; wait for it to finish before running this one";
    }

    private void Busy() => _ring.IsActive = _refreshQ.Busy || _startQ.Busy;

    /// <summary>Current values on the UI: computed defaults + those the user changed.</summary>
    private Values Merge()
    {
        var v = _resolved.Clone();
        foreach (var (k, val) in _values) v[k] = val;
        return v;
    }

    /// <summary>Recompute defaults and check.</summary>
    private void Refresh()
    {
        var v = _values.Clone();
        _refreshQ.Run(() => Context.Query(_win.Cfg, ctx => _op.Prepare(ctx, v)),
            p => { ApplyPrepared(p); Busy(); },
            e => { Busy(); _status.Text = $"Error: {e.Message}"; });
        Busy();
    }

    private void ApplyPrepared(Operation.Prepared p)
    {
        (_resolved, _fields, _last) = (p.Values, p.Fields, p.Checks);
        _form.Show(_fields, Merge());
        _form.ShowProblems(p.Checks);
        _checks.Show(p.Checks, _op.Steps);
        UpdateButtons();
    }

    private void RunOp()
    {
        if (Jobs.RunningFor(_op) is { } r) { Ui.Error(XamlRoot, "Running", $"'{r.Op.Title}' is running{(r.Local ? "" : $" in {r.Who}")}; wait for it to finish. Other menu items can run at the same time"); return; }
        // Check again at the moment of the press: the check result may be stale
        var v = _values.Clone();
        _startQ.Run(() => Context.Query(_win.Cfg, ctx => _op.Prepare(ctx, v)), async p =>
        {
            Busy();
            ApplyPrepared(p);
            if (p.Checks.Fails > 0) return;
            if (_op.Mutates && !await Confirm(p.Checks)) return;
            try { Attach(Jobs.Start(_win.Cfg, _op, p.Values)); }
            catch (WlbError e) { Ui.Error(XamlRoot, "Run", e.Message); }
        }, e => { Busy(); _status.Text = $"Error: {e.Message}"; });
        Busy();
    }

    /// <summary>Confirmation dialog: states clearly what will be done, and lists warnings if there are any.</summary>
    private async System.Threading.Tasks.Task<bool> Confirm(CheckList checks)
    {
        var sp = new StackPanel { Spacing = 8, MaxWidth = 480 };
        sp.Children.Add(Ui.Text(_op.Summary));
        foreach (var s in _op.Stages.Select((s, i) => $"{i + 1}. {s}")) sp.Children.Add(Ui.Text(s, 13, dim: true));
        foreach (var c in checks.Problems) sp.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = c.Item, Message = c.Detail });
        var d = new ContentDialog
        {
            Title = $"Start {_op.Title}?", Content = new ScrollViewer { Content = sp },
            PrimaryButtonText = "Start", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        return await Ui.Show(d) == ContentDialogResult.Primary;
    }

    private void ShowRun(bool run)
    {
        _runView.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        _checksView.Visibility = run ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Attach(Job job)
    {
        if (_live && _job is { Local: true } old) old.Out.Added -= OnAdded;
        _job = job;
        _live = job.Running;
        _view = new JobView(job.Op);
        _lastSeq = 0;
        _tail = null;
        _runPanel.Start(_view, job);
        if (job.Local)
        {
            job.Out.Added += OnAdded;   // Subscribe first, then replay, so nothing produced in between is missed; Flush deduplicates by sequence number
            foreach (var it in job.Out.Since(0)) _pending.Enqueue(it);
        }
        else
        {
            _tail = new LogTail(job.LogPath);
            _runPanel.AddLog($"(running in {job.Who}, process {job.Pid}; its log is below: {job.LogPath})");
        }
        ShowRun(true);
        _flush.Start();
        _runBtn.IsEnabled = false;
        RenderRun();
    }

    private void RenderRun()
    {
        if (_view is not null && _job is not null) _runPanel.Show(_view, _job);
    }

    /// <summary>Called on the execution thread: only enqueues, the UI thread takes items on a timer.</summary>
    private void OnAdded(BufferedOutput.Item it) => _pending.Enqueue(it);

    private void Flush()
    {
        if (_view is null) return;
        if (_tail is not null)
            foreach (var l in _tail.ReadNew()) { _view.ApplyLog(l); _runPanel.AddLog(l); }
        while (_pending.TryDequeue(out var it))
        {
            if (it.Kind != "progress" && it.Seq <= _lastSeq) continue;
            _lastSeq = Math.Max(_lastSeq, it.Seq);
            if (_view.Apply(it) is { } line) _runPanel.AddLog(line);
        }
        RenderRun();
        if (!_live && _pending.IsEmpty) _flush.Stop();
    }

    private async void Import()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_win));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            Prefill(_op.FromPreset(Preset.Load(file.Path)));
            _status.Text = $"Imported {file.Path}";
        }
        catch (Exception e) { Ui.Error(XamlRoot, "Import preset", e.Message); }
    }

    private async void ImportFromVhd()
    {
        const string title = "Import from VHD";
        try
        {
            var names = await System.Threading.Tasks.Task.Run(() => Context.Query(_win.Cfg, ctx => ctx.ChildDisks().Select(d => d.Name).ToList()));
            if (names.Count == 0) { Ui.Error(XamlRoot, title, "No child disks available"); return; }
            var box = new ComboBox { ItemsSource = names, SelectedIndex = 0, MinWidth = 360 };
            var dlg = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "Import", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            if (await Ui.Show(dlg) != ContentDialogResult.Primary) return;
            var name = (string)box.SelectedItem;
            _status.Text = $"Reading {name}...";
            var v = await System.Threading.Tasks.Task.Run(() => Context.Query(_win.Cfg, ctx => ((Ops.LayerOp)_op).FromVhd(ctx, ctx.Cfg.VhdPath(name))));
            Prefill(v);
            _status.Text = $"Imported settings from {name}";
        }
        catch (Exception e) { _status.Text = ""; Ui.Error(XamlRoot, title, e.Message); }
    }

    private async void Export()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(Preset.FileName(_op.Name)) };
            picker.FileTypeChoices.Add("Preset file", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_win));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            _op.ToPreset(Merge(), _fields, "wlb gui").Save(file.Path);
            _status.Text = $"Exported to {file.Path}";
        }
        catch (Exception e) { Ui.Error(XamlRoot, "Export preset", e.Message); }
    }
}
