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
using TgCheckState = Terminal.Gui.Views.CheckState;

namespace WinLayerBoot.Tui;

/// <summary>wlb tui: Terminal.Gui TUI (mouse supported). Navigation on the left, pages on the right, with the same paging as Web/WinUI.</summary>
public static class TuiApp
{
    public static int Run(string[] args)
    {
        Config cfg;
        try { cfg = Config.Load(); }
        catch (WlbError e) { ConsoleWrite.Write($"Error: {e.Message}\n"); return 1; }
        using IApplication app = Application.Create();
        app.Init();
        Console.Title = BuildInfo.AppTitle;
        using var main = new MainView(app, cfg);
        app.Run(main);
        main.Shutdown();
        return 0;
    }
}

/// <summary>Page: created the first time it is switched to and resident afterwards (entered values and logs remain); notified when switched to, when a job starts / ends, and on F5 (all on the UI thread).</summary>
internal abstract class TuiPage : View
{
    protected TuiPage()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
    }

    public virtual void Shown() { }
    public virtual void JobChanged(Job job) { }
    public virtual void Refresh() { }
}

/// <summary>Main window: navigation + pages + status bar. Operations being executed are marked with ● after the navigation item, and the title row shows elapsed time.</summary>
internal sealed class MainView : Window
{
    private readonly IApplication _app;
    private readonly ListView _nav;
    private readonly View _body;
    private readonly Label _head;
    private readonly Dictionary<string, TuiPage> _pages = new(StringComparer.OrdinalIgnoreCase);
    private TuiPage? _page;
    private bool _ticking;

    private static readonly (string Tag, string Label)[] Pages =
    [
        ("overview", "Overview"), ("base", "Base disk"), ("layer", "Child disk"), ("snapshot", "Snapshot"), ("export", "Export"), ("display", "Display"),
        ("entries", "Boot entries"), ("settings", "Settings"),
    ];

    private const string Keys = "↑↓ select page, Enter/Tab open page, Esc exit, F5 refresh, mouse supported";

    public Config Cfg { get; }
    public IApplication App => _app;

    public MainView(IApplication app, Config cfg)
    {
        _app = app;
        Cfg = cfg;
        Jobs.Frontend = "TUI";
        Jobs.Watch();
        cfg.Watch();
        cfg.Changed += () => Post(() => { if (_pages.GetValueOrDefault("settings") is SettingsPage s) s.Shown(); });
        System.Threading.Tasks.Task.Run(() => { try { Context.Query(cfg, c => { c.Mnt.CleanStale(); return 0; }); } catch { } });
        BorderStyle = LineStyle.None;
        // Without a border there is no title bar, so the title takes its own row
        _head = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = 0, Width = Dim.Fill(), Text = HeadText([]), SchemeName = "Menu" };
        _nav = new ListView { X = 0, Y = 1, Width = 14, Height = Dim.Fill(1) };
        _nav.SetSource(new ObservableCollection<string>(NavLabels([])));
        _nav.SelectedItem = 0;   // If not set, the first arrow key press merely turns "nothing selected" into selecting item 0, which looks like no response
        _nav.ValueChanged += (_, _) => Show(Pages[Math.Max(0, _nav.SelectedItem ?? 0)].Tag, focusPage: false);
        _nav.Accepting += (_, e) => { e.Handled = true; _page?.SetFocus(); };
        var sep = new Line { X = 14, Y = 1, Height = Dim.Fill(1), Orientation = Orientation.Vertical };
        _body = new View { X = 16, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        var status = new StatusBar(
        [
            new Shortcut(Key.Esc, "Exit", Quit),
            new Shortcut(Key.F5, "Refresh", () => _page?.Refresh()),
        ]);
        Add(_head, _nav, sep, _body, status);
        Show("overview", focusPage: false);
        Jobs.Changed += OnJobChanged;
        RefreshRunning();   // Already running in another program
        // SetFocus in the constructor does not count (not running yet): after initialization completes, and again a short while after it starts running, put focus on the navigation
        Initialized += (_, _) => _nav.SetFocus();
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () => { _nav.SetFocus(); return false; });
    }

    /// <summary>Jobs run on a background thread in this process and are cut off when the process exits: if this process has a job running, ask first; Enter and Esc both mean do not exit.</summary>
    private void Quit()
    {
        if (Jobs.Running().FirstOrDefault(j => j.Local) is { } job
            && MessageBox.Query(_app, "Job still running", $"'{job.Op.Title}' is not finished. Exiting now will interrupt it and may leave a half-written disk that is still mounted.", "Exit anyway", "Keep running") != 0) return;
        _app.RequestStop();
    }

    public void Shutdown()
    {
        Jobs.Changed -= OnJobChanged;
        foreach (var p in _pages.Values.Where(p => p != _page)) p.Dispose();
        IsoPool.DetachAll();
    }

    /// <summary>Post back to the UI thread for execution (results from background threads, execution output).</summary>
    public void Post(Action a) => _app.Invoke(a);

    public bool IsShown(View page) => _page == page;

    /// <summary>Jump from another page to an operation and prefill values (used by buttons on the boot entries page).</summary>
    public void Go(string tag, Values? prefill = null)
    {
        var i = Array.FindIndex(Pages, p => p.Tag == tag);
        if (i >= 0 && _nav.SelectedItem != i) _nav.SelectedItem = i;   // Triggers ValueChanged → Show
        else Show(tag, focusPage: true);
        if (prefill is not null && _pages.GetValueOrDefault(tag) is OpPage op) op.Prefill(prefill);
        _page?.SetFocus();
    }

    private void Show(string tag, bool focusPage)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "overview" => new OverviewPage(this),
                "entries" => new EntriesPage(this),
                "settings" => new SettingsPage(this),
                _ => new OpPage(this, Operations.Find(tag)!),
            };
            _pages[tag] = page;
        }
        if (_page != page)
        {
            if (_page is not null) _body.Remove(_page);
            _page = page;
            _body.Add(page);
        }
        page.Shown();
        if (focusPage) page.SetFocus();
    }

    private static IEnumerable<string> NavLabels(List<Job> running) =>
        Pages.Select(p => " " + p.Label + (running.Any(j => j.Lock == p.Tag) ? " ●" : ""));

    private static string HeadText(List<Job> running) =>
        $" {BuildInfo.AppTitle} (wlb) —— {Environment.MachineName}   " + (running.Count == 0 ? Keys
            : "● Running: " + string.Join(", ", running.Select(j => $"{j.Op.Title} {DateTime.Now - j.StartedAt:m\\:ss}{(j.Local ? "" : $" ({j.Who})")}")));

    private string HeadNow(List<Job> running) =>
        HeadText(running) + (running.Count == 1 && _pages.Values.OfType<OpPage>().FirstOrDefault(p => p.Lock == running[0].Lock)?.Headline is { Length: > 0 } now ? $"  {now}" : "");

    private void OnJobChanged(Job job) => Post(() =>
    {
        RefreshRunning();
        foreach (var p in _pages.Values) p.JobChanged(job);
    });

    /// <summary>Menu items being executed in all processes: marked with ● after the navigation item, and listed in the title row with a running clock.</summary>
    private void RefreshRunning()
    {
        var running = Jobs.Running();
        var sel = _nav.SelectedItem;
        _nav.SetSource(new ObservableCollection<string>(NavLabels(running)));
        _nav.SelectedItem = sel;
        _head.Text = HeadNow(running);
        if (running.Count == 0 || _ticking) return;
        _ticking = true;
        _app.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            var r = Jobs.Running();
            _head.Text = HeadNow(r);
            return _ticking = r.Count > 0;
        });
    }
}

/// <summary>Common part of list pages (overview, boot entries): reads data in the background (only the last request counts); after a job ends the data is stale, and it is re-read immediately if shown, or when switched to if not shown.</summary>
internal abstract class ListPage<T> : TuiPage
{
    protected readonly MainView Main;
    protected readonly Label Status = new() { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "" };
    private readonly Latest _q;


    protected ListPage(MainView main)
    {
        Main = main;
        _q = new Latest(main.Post);
    }

    protected abstract T Read(Context ctx);
    protected abstract void Render(T data);

    public override void Shown() => Refresh();

    public override void JobChanged(Job job)
    {
        if (job.Running) return;
        if (Main.IsShown(this)) Refresh();
    }

    public override void Refresh()
    {
        Status.Text = "Loading...";
        _q.Run(() => Context.Query(Main.Cfg, Read),
            d => { Status.Text = $"{DateTime.Now:HH:mm:ss} Loaded (F5 to refresh)"; Render(d); },
            e => Status.Text = $"Failed to load: {e.Message}");
    }
}

/// <summary>Settings: drawn from fields like the operation pages (Config.Fields); the boot menu language can only be chosen from a list.</summary>
internal sealed class SettingsPage : TuiPage
{
    private readonly MainView _main;
    private readonly TuiForm _form;
    private readonly Values _values = new();   // Changed, not yet saved
    private readonly Label _resolved, _status;

    public SettingsPage(MainView main)
    {
        _main = main;
        var head = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 0, Y = 0, Text = $"Shared settings for all four interfaces: {main.Cfg.LoadedFrom}" };
        _form = new TuiForm(main) { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(6), BorderStyle = LineStyle.Rounded, Title = "Settings" };
        _resolved = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = 1, Y = Pos.AnchorEnd(5), Width = Dim.Fill(1), Height = 3 };
        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save" };
        var discard = new Button { X = Pos.Right(save) + 1, Y = Pos.AnchorEnd(1), Text = "Discard changes" };
        _status = new Label { HotKeySpecifier = Dialogs.NoHotKey, X = Pos.Right(discard) + 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "" };
        save.Accepting += (_, e) => { e.Handled = true; Save(); };
        discard.Accepting += (_, e) => { e.Handled = true; _values.Clear(); Refresh(); _status.Text = ""; };
        _form.Changed += (k, v, rebuild) => { _values[k] = v; _status.Text = "Unsaved changes"; if (rebuild) Refresh(); };
        Add(head, _form, _resolved, save, discard, _status);
        Refresh();
    }

    public override void Shown() { if (_values.Count == 0) Refresh(); }   // Files in the iso directory may have changed

    public override void Refresh()
    {
        var cfg = _main.Cfg;
        var fields = cfg.Fields();
        var v = new Values();
        foreach (var f in fields) v[f.Key] = f.Default;
        foreach (var (k, val) in _values) v[k] = val;
        _form.Show(fields, v);
        _resolved.Text = $"ISOs in use:\n LOF    {Or(cfg.ResolveLofIso())}\n virtio {Or(cfg.ResolveVirtioIso())}";
    }

    private void Save()
    {
        var cfg = _main.Cfg;
        try
        {
            cfg.Update(_values);   // Re-read the file under the lock and change only these items, so as not to overwrite what someone else just saved
            _values.Clear();
            Refresh();
            _status.Text = $"Saved to {cfg.LoadedFrom}";
        }
        catch (WlbError ex) { _status.Text = ex.Message; }
    }

    private static string Or(string s) => s == "" ? "(not found)" : s;
}
