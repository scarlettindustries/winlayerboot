using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinLayerBoot.Core;

/// <summary>An expected error in the flow: only the message is shown to the user, no stack trace is printed.</summary>
public sealed class WlbError(string message) : Exception(message);

public enum Level { Info, Title, Ok, Warn, Error, Dim }

/// <summary>Where output goes: the console, the TUI log panel, the Web event stream, the WinUI log box each implement one.</summary>
public interface IOutput
{
    void Line(string text, Level level);
    /// <summary>A line refreshed in place (progress); percent is -1 when absent.</summary>
    void Progress(string text, int percent = -1);
    /// <summary>Enters step no (starting from 1; 0 for steps not in the plan). The text-mode title goes through Line separately; graphical frontends use this to draw the step bar.</summary>
    void Stage(int no, int total, string label, string detail, bool skipped);
    /// <summary>One item of the result (key-value). The text mode goes through Line separately.</summary>
    void Result(string key, string value);
    /// <summary>The result of the pre-execution checks. The text-mode table goes through Line separately.</summary>
    void Checks(CheckList checks);
}

/// <summary>Output of the execution process, also written to the log file.</summary>
public sealed class Output(IOutput sink)
{
    private StreamWriter? _log;
    private readonly object _gate = new();

    public IOutput Sink { get; } = sink;
    public string? LogPath { get; private set; }
    /// <summary>The steps planned for this operation (Operation.Stages).</summary>
    public string[] Plan { get; set; } = [];

    public void StartLog(string path)
    {
        lock (_gate)
        {
            _log = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
            LogPath = path;
            _log.WriteLine($"WLB log started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
    }

    public void StopLog()
    {
        lock (_gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    public void Say(string text, Level level = Level.Info)
    {
        Sink.Line(text, level);
        lock (_gate) _log?.WriteLine(text);
    }

    public void Warn(string text) => Say(text, Level.Warn);
    public void Error(string text) => Say(text, Level.Error);
    public void Ok(string text) => Say(text, Level.Ok);
    public void Dim(string text) => Say(text, Level.Dim);

    public void Title(string text)
    {
        Say("");
        Say("==== " + text, Level.Title);
    }

    /// <summary>Enters a step in the plan. In the log and on the command line it is "==== 2/6 name: description"; when viewing another process's job, this format is used to restore the steps (JobView.ApplyLog).</summary>
    public void Stage(string label, string detail = "", bool skipped = false)
    {
        var no = Array.IndexOf(Plan, label) + 1;
        Sink.Stage(no, Plan.Length, label, detail, skipped);
        Title((no > 0 ? $"{no}/{Plan.Length} {label}" : label) + (detail != "" ? ": " + detail : ""));
    }

    public void Result(string key, string value)
    {
        Sink.Result(key, value);
        Say($"  {TextTable.Pad(key, 8)} {value}");
    }

    public void Checks(CheckList checks)
    {
        Sink.Checks(checks);
        foreach (var l in checks.TextLines()) Say(l);
    }

    public void Progress(string what, int percent)
    {
        Sink.Progress($"  {what} {percent}%", percent);
    }

    public void Table(string[] headers, IEnumerable<string[]> rows, Level level = Level.Info)
    {
        foreach (var l in TextTable.Format(headers, rows)) Say(l, level);
    }
}

/// <summary>Discards output (background queries of the UI, computing defaults in help).</summary>
public sealed class NullOutput : IOutput
{
    public void Line(string text, Level level) { }
    public void Progress(string text, int percent = -1) { }
    public void Stage(int no, int total, string label, string detail, bool skipped) { }
    public void Result(string key, string value) { }
    public void Checks(CheckList checks) { }
}

/// <summary>Progress line de-duplication: lets a line through only when the integer part of the percentage changed (used by the log and the output boxes of TUI and WinUI; the progress callback fires several times a second).</summary>
public sealed class ProgressFilter
{
    private string _last = "";

    public bool Pass(string text)
    {
        var key = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?=\.\d+%|%)").Value;
        if (key == _last) return false;
        _last = key;
        return true;
    }
}

/// <summary>Plain-text table, shared by TUI and CLI.</summary>
public static class TextTable
{
    public static List<string> Format(string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var w = headers.Select((h, i) => Math.Max(Width(h), all.Count == 0 ? 0 : all.Max(r => Width(r[i])))).ToArray();
        string Line(string[] cells) => string.Join(" ", cells.Select((c, i) => i == cells.Length - 1 ? c : Pad(c, w[i]))).TrimEnd();
        List<string> list = ["", Line(headers), Line(w.Select(n => new string('-', n)).ToArray())];
        list.AddRange(all.Select(Line));
        list.Add("");
        return list;
    }

    public static int Width(string s) => s.Sum(c => IsWide(c) ? 2 : 1);
    public static string Pad(string s, int width) => s + new string(' ', Math.Max(0, width - Width(s)));

    private static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6);
}

/// <summary>Console output: colored, progress lines refreshed in place with carriage return; when output is redirected, progress lines are printed as normal lines (de-duplicated).</summary>
public sealed class ConsoleOutput : IOutput
{
    private bool _progressOpen;

    // Text mode only looks at Line
    public void Stage(int no, int total, string label, string detail, bool skipped) { }
    public void Result(string key, string value) { }
    public void Checks(CheckList checks) { }
    private readonly ProgressFilter _filter = new();

    public void Line(string text, Level level)
    {
        EndProgress();
        var colored = !Console.IsOutputRedirected;
        var color = level switch
        {
            Level.Title => ConsoleColor.Cyan,
            Level.Ok => ConsoleColor.Green,
            Level.Warn => ConsoleColor.Yellow,
            Level.Error => ConsoleColor.Red,
            Level.Dim => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray,
        };
        var old = colored ? Console.ForegroundColor : ConsoleColor.Gray;
        if (colored) Console.ForegroundColor = color;
        try { ConsoleWrite.Write(text + "\n"); }
        finally { if (colored) Console.ForegroundColor = old; }
    }

    public void Progress(string text, int percent = -1)
    {
        if (Console.IsOutputRedirected)
        {
            // When redirected, print a line only when the integer percentage changes, so the log is not full of progress
            if (_filter.Pass(text)) ConsoleWrite.Write(text.Trim() + "\n");
            return;
        }
        ConsoleWrite.Write("\r" + text);
        _progressOpen = true;
    }

    private void EndProgress()
    {
        if (!_progressOpen) return;
        ConsoleWrite.Write("\n");
        _progressOpen = false;
    }
}

/// <summary>
/// Writes text to the console: when output is redirected, writes UTF-8 directly; when output goes to a console, uses WriteConsoleW, independent of the code page.
/// With no standard output (double-click launch), attaches to the parent process's console.
/// </summary>
public static class ConsoleWrite
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleW(IntPtr h, string s, int n, out int written, IntPtr reserved);

    /// <summary>The console is used only by this process: the system created it specially when launched by double-click or a scheduled task; when launched from the command line the shell also uses it.</summary>
    public static bool OwnsConsole()
    {
        var list = new uint[2];
        return GetConsoleProcessList(list, (uint)list.Length) == 1;
    }

    /// <summary>Frees the console if it is exclusive to this process; left alone when launched from the command line.</summary>
    public static void DetachIfOwnConsole()
    {
        if (OwnsConsole()) FreeConsole();
    }

    public static void Write(string text)
    {
        text = text.Replace("\n", Environment.NewLine);
        var h = GetStdHandle(StdOutputHandle);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            if (!AttachConsole(AttachParentProcess)) return;
            h = GetStdHandle(StdOutputHandle);
        }
        if (GetConsoleMode(h, out _))
        {
            WriteConsoleW(h, text, text.Length, out _, IntPtr.Zero);
        }
        else
        {
            using var s = Console.OpenStandardOutput();
            var b = new UTF8Encoding(false).GetBytes(text);
            s.Write(b, 0, b.Length);
        }
    }

    private static StreamReader? _in;

    /// <summary>Reads a line of input; when input is redirected, reads as UTF-8; returns null at the end of the input stream.</summary>
    public static string? ReadLine(string prompt)
    {
        Write(prompt);
        if (Console.IsInputRedirected)
        {
            _in ??= new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            return _in.ReadLine();
        }
        return Console.ReadLine();
    }
}

/// <summary>Collected output (Web jobs, tests): each entry is numbered for incremental fetching.</summary>
public sealed class BufferedOutput : IOutput
{
    /// <summary>Kind: line / progress / stage (Text is the step name) / result (Text is the key, Detail is the value) / checks.</summary>
    public sealed record Item(long Seq, string Kind, string Text, Level Level)
    {
        public string Detail { get; init; } = "";
        public int No { get; init; }
        public int Total { get; init; }
        public bool Skipped { get; init; }
        public int Percent { get; init; } = -1;
        public CheckList? Checks { get; init; }
    }

    private readonly List<Item> _items = [];
    private readonly object _gate = new();
    private long _seq;
    public event Action<Item>? Added;

    public void Line(string text, Level level) => Add(new Item(0, "line", text, level));
    public void Progress(string text, int percent = -1) => Add(new Item(0, "progress", text, Level.Dim) { Percent = percent });
    public void Stage(int no, int total, string label, string detail, bool skipped) => Add(new Item(0, "stage", label, Level.Title) { No = no, Total = total, Detail = detail, Skipped = skipped });
    public void Result(string key, string value) => Add(new Item(0, "result", key, Level.Info) { Detail = value });
    public void Checks(CheckList checks) => Add(new Item(0, "checks", "", Level.Info) { Checks = checks });

    private void Add(Item item)
    {
        Item it;
        lock (_gate)
        {
            it = item with { Seq = ++_seq };
            // Keep only the last progress line
            if (it.Kind == "progress" && _items.Count > 0 && _items[^1].Kind == "progress") _items[^1] = it;
            else _items.Add(it);
        }
        Added?.Invoke(it);
    }

    public List<Item> Since(long seq)
    {
        lock (_gate) return _items.Where(i => i.Seq > seq).ToList();
    }

    public string AllText()
    {
        lock (_gate) return string.Join("\n", _items.Where(i => i.Kind == "line").Select(i => i.Text));
    }
}
