using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinLayerBoot.Core;

public enum StepState { Pending, Running, Done, Skipped, Failed }

public sealed class StepView(string label)
{
    public string Label { get; } = label;
    public string Detail { get; set; } = "";
    public StepState State { get; set; }
}

/// <summary>
/// How one execution looks in the UI: step bar, current progress, result, notices. The window and the TUI both feed job events into it and draw from it (the web's equivalent is web/src/job.ts).
/// Jobs of this process feed events (Apply); jobs of other processes have only a log file and feed log lines (ApplyLog), which can restore steps and notices but not percentages or results.
/// </summary>
public sealed class JobView(Operation op)
{
    public List<StepView> Steps { get; } = op.Stages.Select(s => new StepView(s)).ToList();
    /// <summary>Percentage of the current step, -1 if none (the UI draws an indeterminate progress bar).</summary>
    public int Percent { get; private set; } = -1;
    /// <summary>What is being done now: the latest output line.</summary>
    public string Activity { get; private set; } = "";
    public List<(string Key, string Value)> Results { get; } = [];
    /// <summary>Messages the user needs to notice during execution (warning-level output).</summary>
    public List<string> Warnings { get; } = [];
    public string State { get; private set; } = "running";
    public string Error { get; private set; } = "";

    public StepView? Current => Steps.FirstOrDefault(s => s.State == StepState.Running);

    /// <summary>One-line progress (for the window title and navigation bar).</summary>
    public string Headline
    {
        get
        {
            var c = Current;
            if (c is null) return Activity;
            var no = Steps.IndexOf(c) + 1;
            return $"Step {no}/{Steps.Count}: {c.Label}{(Percent >= 0 ? $" {Percent}%" : "")}";
        }
    }

    /// <summary>Feeds one event; returns the text if it should go into the log box.</summary>
    public string? Apply(BufferedOutput.Item it)
    {
        switch (it.Kind)
        {
            case "stage": Enter(it.No, it.Detail, it.Skipped); return null;
            case "progress":
                Percent = it.Percent;
                // "Unpacking image 42%": the percentage is shown separately; if the name equals the step, do not repeat it
                var what = PercentSuffix.Replace(it.Text, "").Trim();
                Activity = Current?.Label.StartsWith(what) == true ? "" : what;
                return null;
            case "result": Results.Add((it.Text, it.Detail)); return null;
            case "checks": return null;
            default: Line(it.Text, it.Level); return it.Text;
        }
    }

    /// <summary>Feeds one log file line (format see Output.Stage).</summary>
    public void ApplyLog(string line)
    {
        var m = StageLine.Match(line);
        if (m.Success) Enter(int.Parse(m.Groups[1].Value), m.Groups[2].Value, false);
        else Line(line, Level.Info);
    }

    public void Finish(string state, string error)
    {
        State = state;
        Error = error;
        Percent = -1;
        foreach (var s in Steps)
        {
            if (s.State == StepState.Running) s.State = state == "done" ? StepState.Done : StepState.Failed;
        }
    }

    private void Enter(int no, string detail, bool skipped)
    {
        Percent = -1;
        Activity = "";
        if (no < 1 || no > Steps.Count) return;
        foreach (var s in Steps.Take(no - 1))
            if (s.State is StepState.Running or StepState.Pending) s.State = StepState.Done;
        var cur = Steps[no - 1];
        cur.Detail = detail;
        cur.State = skipped ? StepState.Skipped : StepState.Running;
    }

    private void Line(string text, Level level)
    {
        var t = text.Trim();
        if (t == "" || level == Level.Title) return;
        if (level == Level.Warn) Warnings.Add(t);
        if (level is Level.Info or Level.Ok) Activity = t;
    }

    private static readonly Regex StageLine = new(@"^==== (\d+)/\d+ [^:]+(?::\s*(.*))?$");
    private static readonly Regex PercentSuffix = new(@"\s*\d+%\s*$");
}
