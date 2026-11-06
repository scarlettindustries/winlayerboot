using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinLayerBoot.Core;
using WinLayerBoot.Gui;

namespace WinLayerBoot;

/// <summary>
/// WinUI window: navigation on the left, pages on the right, with the same paging as Web/TUI. Pages are all built in code under Gui\, created the first time they are switched to, and resident afterwards.
/// Menu items being executed (including those started by this program and by other programs or people) are marked with ● after the navigation item, and listed at the bottom of the navigation bar with elapsed time (clicking one jumps to that page).
/// </summary>
public sealed partial class MainWindow : Window
{
    public Config Cfg { get; }
    public DispatcherQueue Dq { get; } = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _runningText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
    private readonly StackPanel _running = new() { Margin = new Thickness(16, 8, 8, 8), Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _tick;
    private bool _navigating, _closed, _closeAnyway;

    public MainWindow()
    {
        InitializeComponent();
        Config cfg;
        try { cfg = Config.Load(); }
        catch (WlbError) { cfg = new Config(); }
        Cfg = cfg;
        Jobs.Frontend = "window";
        Jobs.Watch();
        cfg.Watch();
        cfg.Changed += () => Post(() => { if (_pages.GetValueOrDefault("settings") is SettingsPage s) s.Shown(); });
        // Clean up mount points left by a previous abnormal exit: done in the background so opening the window is not delayed
        Task.Run(() => { try { Context.Query(cfg, c => { c.Mnt.CleanStale(); return 0; }); } catch { } });
        Title = $"{BuildInfo.AppTitle} —— {Environment.MachineName}";
        _running.Children.Add(new ProgressBar { IsIndeterminate = true });
        _running.Children.Add(_runningText);
        Nav.Loaded += (_, _) => { if (Nav.SettingsItem is NavigationViewItem s) s.Content = "Settings"; };   // Settings items exist only after the template is applied
        _running.Tapped += (_, _) => { if (Jobs.Running().FirstOrDefault() is { } j) Go(j.Lock); };
        Nav.PaneFooter = _running;
        _tick = Dq.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => ShowRunning(Jobs.Running());
        Jobs.Changed += OnJobChanged;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            // After the window is closed, controls must not be touched: stop the timer and queued updates, otherwise closing the window while a job runs will crash
            _closed = true;
            Jobs.Changed -= OnJobChanged;
            _tick.Stop();
            foreach (var p in _pages.Values.OfType<OpPage>()) p.Close();
            IsoPool.DetachAll();
        };
        Nav.SelectedItem = Nav.MenuItems[0];
        RefreshRunning();   // Already running in another program
    }

    /// <summary>Jobs run on a background thread in this process and are cut off when the process exits: if this process has a job running, ask first, and the default is not to close.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_closeAnyway || Jobs.Running().FirstOrDefault(j => j.Local) is not { } job) return;
        e.Cancel = true;
        var ask = new ContentDialog
        {
            Title = "Job still running",
            Content = $"'{job.Op.Title}' is not finished. Closing now will interrupt it and may leave a half-written disk that is still mounted.",
            PrimaryButtonText = "Close anyway",
            CloseButtonText = "Keep running",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await Ui.Show(ask) != ContentDialogResult.Primary) return;
        _closeAnyway = true;
        Close();
    }

    /// <summary>Post back to the UI thread for execution.</summary>
    public void Post(Action a) => Dq.TryEnqueue(() => { if (!_closed) a(); });

    public bool IsShown(UIElement page) => Body.Children.Count > 0 && ReferenceEquals(Body.Children[0], page);

    /// <summary>Jump to a page and prefill values (used by buttons on the boot entries page).</summary>
    public void Go(string tag, Values? prefill = null)
    {
        var nvi = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);
        if (nvi is not null && !ReferenceEquals(Nav.SelectedItem, nvi))
        {
            _navigating = true;
            try { Nav.SelectedItem = nvi; }
            finally { _navigating = false; }
        }
        Show(tag);
        if (prefill is not null && _pages.GetValueOrDefault(tag) is OpPage p) p.Prefill(prefill);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navigating) return;
        Show(args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "overview");
    }

    private void Show(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "overview" => new OverviewPage(this),
                "entries" => new EntriesPage(this),
                "settings" => new SettingsPage(this),
                _ => Operations.Find(tag) is { } op ? new OpPage(this, op) : new TextBlock { Text = $"No page {tag}" },
            };
            _pages[tag] = page;
        }
        if (!IsShown(page))
        {
            Body.Children.Clear();
            Body.Children.Add(page);
        }
        (page as IPage)?.Shown();
    }

    private void OnJobChanged(Job job) => Post(() =>
    {
        RefreshRunning();
        foreach (var p in _pages.Values.OfType<IPage>()) p.JobChanged(job);
    });

    /// <summary>Menu items being executed in all processes: the navigation item gets an InfoBadge, and they are listed at the bottom with a running clock.</summary>
    private void RefreshRunning()
    {
        var running = Jobs.Running();
        foreach (var nvi in Nav.MenuItems.OfType<NavigationViewItem>())
            if (nvi.Tag is string t) nvi.InfoBadge = running.Any(j => j.Lock == t) ? new InfoBadge() : null;
        if (running.Count > 0) _tick.Start();
        else _tick.Stop();
        ShowRunning(running);
    }

    private void ShowRunning(List<Job> running)
    {
        _running.Visibility = running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _runningText.Text = string.Join("\n", running.Select(j =>
        {
            var now = _pages.Values.OfType<OpPage>().FirstOrDefault(p => p.Lock == j.Lock)?.Headline;
            return $"{j.Op.Title}  {DateTime.Now - j.StartedAt:m\\:ss}{(string.IsNullOrEmpty(now) ? "" : $"\n{now}")}{(j.Local ? "" : $"\n{j.Who}")}";
        }));
    }
}
