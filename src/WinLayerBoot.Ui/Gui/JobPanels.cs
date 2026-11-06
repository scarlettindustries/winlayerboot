using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinLayerBoot.Core;

namespace WinLayerBoot.Gui;

/// <summary>Status before execution: a one-line conclusion (InfoBar), problems to handle (click to jump to the matching input box), passed items collapsed, accompanying tables, what is about to be done.</summary>
internal sealed partial class CheckPanel : StackPanel
{
    public event Action<string>? FieldClicked;
    private bool _fineOpen;

    public CheckPanel() => Spacing = 8;

    public void Show(CheckList checks, string[] steps)
    {
        Children.Clear();
        var fails = checks.Fails;
        Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false,
            Severity = fails > 0 ? InfoBarSeverity.Error : checks.Warns > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            Title = fails > 0 ? $"{fails} problems to resolve" : checks.Warns > 0 ? $"Ready to run, {checks.Warns} warnings" : "Ready to run",
        });
        foreach (var c in checks.Problems) Children.Add(ProblemRow(c));
        var fine = checks.Fine.ToList();
        if (fine.Count > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var c in fine) list.Children.Add(FineRow(c));
            var ex = new Expander
            {
                Header = $"{fine.Count} passed", Content = list, IsExpanded = _fineOpen,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ex.Expanding += (_, _) => _fineOpen = true;
            ex.Collapsed += (_, _) => _fineOpen = false;
            Children.Add(ex);
        }
        if (checks.Extra is { } t) Children.Add(Ui.Card(Table(t)));
        if (steps.Length > 0)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(Ui.Text("What will happen", 14, true));
            for (var i = 0; i < steps.Length; i++) sp.Children.Add(Ui.Text($"{i + 1}. {steps[i]}", 12, dim: true));
            Children.Add(Ui.Card(sp));
        }
    }

    /// <summary>A problem tied to a preset item jumps there on click; problems of the whole operation (permissions, space) are only displayed.</summary>
    private FrameworkElement ProblemRow(CheckItem c)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new FontIcon { Glyph = c.State == CheckState.Fail ? Ui.GlyphError : Ui.GlyphWarn, FontSize = 16, Foreground = Ui.StateBrush(c.StateText), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var t = new StackPanel();
        t.Children.Add(Ui.Text(c.Item, 14, true));
        t.Children.Add(Ui.Text(c.Detail, 12, dim: true));
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        if (c.Field == "")
        {
            var card = Ui.Card(g);
            card.Margin = new Thickness(0);
            return card;
        }
        var b = new Button
        {
            Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
        };
        ToolTipService.SetToolTip(b, "Go to this field");
        b.Click += (_, _) => FieldClicked?.Invoke(c.Field);
        return b;
    }

    private static Grid FineRow(CheckItem c)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new FontIcon { Glyph = c.State == CheckState.Skip ? Ui.GlyphSkip : Ui.GlyphOk, FontSize = 12, Foreground = Ui.StateBrush(c.StateText), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var t = Ui.Text(c.Detail != "" ? $"{c.Item}: {c.Detail}" : c.Item, 12, dim: true);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    private static Grid Table(CheckList.DataTable t)
    {
        var g = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        foreach (var _ in t.Headers) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r <= t.Rows.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = r == 0 ? t.Headers : t.Rows[r - 1];
            for (var i = 0; i < cells.Length; i++)
            {
                var x = Ui.Text(cells[i], 12, bold: r == 0);
                Grid.SetRow(x, r);
                Grid.SetColumn(x, i);
                g.Children.Add(x);
            }
        }
        return g;
    }
}

/// <summary>Status during and after execution: conclusion, step bar (current step with a progress bar), notices, result, expandable detailed log.</summary>
internal sealed partial class RunPanel : StackPanel
{
    private sealed class StepRow
    {
        public readonly Border Icon = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        public readonly TextBlock Label = Ui.Text("");
        public readonly TextBlock Detail = Ui.Text("", 12, dim: true);
        public readonly ProgressBar Bar = new() { Margin = new Thickness(0, 4, 0, 0) };
        public readonly TextBlock Activity = Ui.Text("", 12, dim: true);
        public StepState? Shown;
    }

    private readonly InfoBar _state = new() { IsOpen = true, IsClosable = false };
    private readonly StackPanel _steps = new() { Spacing = 8 };
    private readonly Border _stepsCard;
    private readonly ProgressBar _plainBar = new() { IsIndeterminate = true };
    private readonly TextBlock _plainActivity = Ui.Text("", 12, dim: true);
    private readonly StackPanel _warnings = new() { Spacing = 8 };
    private readonly StackPanel _results = new() { Spacing = 6 };
    private readonly Border _resultsCard;
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, MinHeight = 160 };
    private readonly Expander _logBox;
    private readonly StringBuilder _logText = new();
    private readonly Button _openLog;
    private readonly List<StepRow> _rows = [];
    private int _warnShown, _resultShown;
    private string _logPath = "";

    public RunPanel(Action back)
    {
        Spacing = 8;
        _stepsCard = Ui.Card(_steps);
        _resultsCard = Ui.Card(_results);
        _logBox = new Expander { Header = "Detailed log", Content = _log, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _logBox.Expanding += (_, _) => _log.Text = _logText.ToString();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        _openLog = Ui.Button("Open log file", () => { try { Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true }); } catch { } });
        bar.Children.Add(_openLog);
        bar.Children.Add(Ui.Button("Back to checks", back));
        Children.Add(_state);
        Children.Add(_plainBar);
        Children.Add(_plainActivity);
        Children.Add(_stepsCard);
        Children.Add(_warnings);
        Children.Add(_resultsCard);
        Children.Add(_logBox);
        Children.Add(bar);
    }

    public void Start(JobView v, Job job)
    {
        _steps.Children.Clear();
        _rows.Clear();
        _warnings.Children.Clear();
        _results.Children.Clear();
        _resultsCard.Visibility = Visibility.Collapsed;
        _warnShown = _resultShown = 0;
        _logText.Clear();
        _log.Text = "";
        _logPath = job.LogPath;
        _stepsCard.Visibility = v.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var s in v.Steps)
        {
            var r = new StepRow();
            r.Label.Text = s.Label;
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(r.Icon);
            var t = new StackPanel();
            t.Children.Add(r.Label);
            t.Children.Add(r.Detail);
            t.Children.Add(r.Bar);
            t.Children.Add(r.Activity);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            _steps.Children.Add(g);
            _rows.Add(r);
        }
    }

    public void AddLog(string line)
    {
        if (_logText.Length > 0) _logText.Append('\n');
        _logText.Append(line);
    }

    public void Show(JobView v, Job job)
    {
        var running = v.State == "running";
        var took = (running ? DateTime.Now : job.EndedAt ?? DateTime.Now) - job.StartedAt;
        _state.Severity = running ? InfoBarSeverity.Informational : v.State == "done" ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        _state.Title = running ? $"Running{(job.Local ? "" : $" ({job.Who})")}" : v.State == "done" ? $"{job.Op.Title} completed" : $"{job.Op.Title} failed";
        _state.Message = running ? $"Elapsed {took:m\\:ss}" : v.State == "done" ? $"Took {took:m\\:ss}" : v.Error;
        // An operation without steps has only one indeterminate progress bar
        var plain = running && v.Steps.Count == 0;
        _plainBar.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        _plainActivity.Visibility = plain && v.Activity != "" ? Visibility.Visible : Visibility.Collapsed;
        _plainActivity.Text = v.Activity;
        for (var i = 0; i < _rows.Count; i++) ShowStep(_rows[i], v.Steps[i], v);
        for (; _warnShown < v.Warnings.Count; _warnShown++)
            _warnings.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = v.Warnings[_warnShown] });
        for (; _resultShown < v.Results.Count; _resultShown++)
        {
            _resultsCard.Visibility = Visibility.Visible;
            _results.Children.Add(ResultRow(v.Results[_resultShown].Key, v.Results[_resultShown].Value));
        }
        _openLog.IsEnabled = _logPath != "";
        if (_logBox.IsExpanded && _log.Text.Length != _logText.Length) _log.Text = _logText.ToString();
    }

    private static void ShowStep(StepRow r, StepView s, JobView v)
    {
        if (r.Shown != s.State)
        {
            r.Shown = s.State;
            r.Icon.Child = s.State switch
            {
                StepState.Running => new ProgressRing { Width = 16, Height = 16, IsActive = true },
                StepState.Done => new FontIcon { Glyph = Ui.GlyphOk, FontSize = 14, Foreground = Ui.StateBrush("OK") },
                StepState.Failed => new FontIcon { Glyph = Ui.GlyphError, FontSize = 14, Foreground = Ui.StateBrush("Failed") },
                StepState.Skipped => new FontIcon { Glyph = Ui.GlyphSkip, FontSize = 14, Foreground = Ui.StateBrush("Skipped") },
                _ => new FontIcon { Glyph = Ui.GlyphPending, FontSize = 14, Opacity = 0.5 },
            };
            r.Label.Opacity = s.State == StepState.Pending ? 0.6 : 1;
            r.Label.FontWeight = s.State == StepState.Running ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        var cur = s.State == StepState.Running;
        r.Label.Text = cur && v.Percent >= 0 ? $"{s.Label}  {v.Percent}%" : s.Label;
        r.Detail.Text = s.Detail;
        r.Detail.Visibility = s.Detail != "" ? Visibility.Visible : Visibility.Collapsed;
        r.Bar.Visibility = cur ? Visibility.Visible : Visibility.Collapsed;
        if (cur)
        {
            r.Bar.IsIndeterminate = v.Percent < 0;
            if (v.Percent >= 0) r.Bar.Value = v.Percent;
        }
        r.Activity.Text = cur ? v.Activity : "";
        r.Activity.Visibility = cur && v.Activity != "" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Grid ResultRow(string key, string value)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Text(key, 13, dim: true));
        var v = Ui.Text(value, 13);
        v.IsTextSelectionEnabled = true;
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        var copy = new Button { Content = new FontIcon { Glyph = Ui.GlyphCopy, FontSize = 12 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(copy, "Copy");
        copy.Click += (_, _) =>
        {
            var p = new DataPackage();
            p.SetText(value);
            Clipboard.SetContent(p);
        };
        Grid.SetColumn(copy, 2);
        g.Children.Add(copy);
        return g;
    }
}
