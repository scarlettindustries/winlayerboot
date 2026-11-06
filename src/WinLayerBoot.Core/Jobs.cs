using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WinLayerBoot.Core;

/// <summary>One execution: of this process (output in Out), or of another process read from the state file (log file only).</summary>
public sealed class Job
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public Operation Op { get; init; } = null!;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime? EndedAt { get; internal set; }
    public string Started => StartedAt.ToString("HH:mm:ss");
    public BufferedOutput Out { get; } = new();
    public string State { get; internal set; } = "running";   // running / done / failed
    public string Error { get; internal set; } = "";
    public string LogPath { get; internal set; } = "";
    /// <summary>Who is executing: the frontend and the user (for web, the browser address), for display.</summary>
    public string Who { get; init; } = "";
    public int Pid { get; init; } = Compat.ProcessId;
    public bool Local => Pid == Compat.ProcessId;
    public bool Running => State == "running";
    /// <summary>Lock group (menu item): only one in the same group can execute at a time.</summary>
    public string Lock => Op.Lock;
}

/// <summary>Content of the state file: %ProgramData%\WinLayerBoot\jobs\&lt;lock group&gt;.json, the latest run kept for each lock group.</summary>
public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string Op { get; set; } = "";
    public string State { get; set; } = "";
    public string Error { get; set; } = "";
    public string Who { get; set; } = "";
    public string LogPath { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Ended { get; set; }
    public int Pid { get; set; }
    public long ProcessStart { get; set; }
}

/// <summary>
/// Registry of executions. Exclusive per lock group (menu item), across processes: for the same menu item, whatever the frontend or process, only one can run; different menu items do not affect each other.
/// State is written to state files in the jobs directory; after a resident frontend (TUI, window, web) calls Watch(), it also receives Changed when other processes start or end jobs.
/// </summary>
public static class Jobs
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, Job> _local = [];
    private static readonly long _myStart = Process.GetCurrentProcess().StartTime.ToFileTimeUtc();
    private static long _changes;
    private static Dictionary<string, (string Id, string State)> _seen = [];   // State of each lock group in other processes at the last scan
    private static bool _scanned;
    private static int _scanning;
    private static Timer? _timer;
    private static FileSystemWatcher? _watcher;

    /// <summary>Which frontend this process is (command line / TUI / window / web), written into the state file for others to see.</summary>
    public static string Frontend { get; set; } = "Command line";

    public static string DefaultWho => $"{Frontend} ({Environment.UserName})";

    public static string Dir => Path.Combine(Path.GetDirectoryName(Config.DefaultPath)!, "jobs");

    /// <summary>Cumulative count of job starts / ends (including other processes): a UI that only got two states can tell from it whether any job finished in between.</summary>
    public static long Changes => Interlocked.Read(ref _changes);

    /// <summary>Raised when a job starts or ends (on the execution thread or the scan thread; the UI must switch back to the UI thread itself).</summary>
    public static event Action<Job>? Changed;

    /// <summary>Jobs executing in all processes (at most one per lock group).</summary>
    public static List<Job> Running()
    {
        List<Job> r;
        lock (_gate) r = _local.Values.Where(j => j.Running).ToList();
        foreach (var rec in Records())
            if (rec.Pid != Compat.ProcessId && rec.State == "running" && FromRecord(rec) is { Running: true } j) r.Add(j);
        return r;
    }

    /// <summary>The executing job in the same lock group as this operation.</summary>
    public static Job? RunningFor(Operation op) => Running().FirstOrDefault(j => j.Lock == op.Lock);

    public static Job? Find(string id)
    {
        lock (_gate) if (_local.TryGetValue(id, out var j)) return j;
        return Records().Where(r => r.Id == id).Select(FromRecord).FirstOrDefault(j => j is not null);
    }

    /// <summary>Runs on a background thread: checks again on the execution thread before running (the UI's check result may be stale) and stops if any item failed. Throws WlbError if the lock group is occupied.</summary>
    public static Job Start(Config cfg, Operation op, Values v, string? who = null)
    {
        var job = new Job { Op = op, Who = who ?? DefaultWho };
        IDisposable? held = null;
        Context? ctx = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            held = GlobalLock.TryTake(LockName(op));   // A mutex is bound to its thread: acquire and release it on the execution thread
            if (held is not null)
            {
                ctx = new Context(cfg, job.Out, detachIsos: false);
                job.LogPath = ctx.LogPath;
                Register(job);
            }
            // Release the caller before notifying: the caller waits for ready on the UI thread, and the notification handler has to return to the UI thread, so waiting the other way round would deadlock
            ready.Set();
            if (held is null) return;
            Changed?.Invoke(job);
            using (held)
            {
                string state = "failed", error = "";
                try
                {
                    var (vals, _, checks) = op.Prepare(ctx!, v);
                    ctx!.Title("Pre-run checks");
                    ctx.Out.Checks(checks);
                    if (checks.Fails > 0) throw new WlbError($"{checks.Fails} checks failed, stopped");
                    op.Execute(ctx, vals);
                    ctx.Out.Ok($"{op.Title} done");
                    state = "done";
                }
                catch (WlbError e) { error = e.Message; ctx!.Say($"Error: {e.Message}", Level.Error); }
                catch (Exception e) { error = e.Message; ctx!.Say($"Error: {e.Message}", Level.Error); ctx.Say(e.ToString(), Level.Dim); }
                finally
                {
                    try { ctx!.Cleanup(); } catch { }
                    Finish(job, state, error);   // Finish writing the state while still holding the lock, so the next one can start
                }
            }
        }) { IsBackground = true, Name = $"wlb-{op.Name}" };
        t.Start();
        ready.Wait();
        if (held is null) throw new WlbError(Busy(op));
        return job;
    }

    /// <summary>The command line runs on this thread: likewise exclusive per lock group and writes the state file (visible to other frontends).</summary>
    public static void RunHere(Context ctx, Operation op, Values vals)
    {
        using var held = GlobalLock.TryTake(LockName(op)) ?? throw new WlbError(Busy(op));
        var job = new Job { Op = op, Who = DefaultWho, LogPath = ctx.LogPath };
        Begin(job);
        string state = "failed", error = "";
        try
        {
            op.Execute(ctx, vals);
            state = "done";
        }
        catch (Exception e) { error = e.Message; throw; }
        finally { Finish(job, state, error); }
    }

    /// <summary>Called by resident frontends: watches the state files and raises Changed when other processes start / end jobs (or exit abnormally).</summary>
    public static void Watch()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Scan(), null, 0, 2000);
        }
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher = new FileSystemWatcher(Dir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
            _watcher.Changed += (_, _) => _timer?.Change(150, 2000);
            _watcher.Created += (_, _) => _timer?.Change(150, 2000);
            _watcher.Renamed += (_, _) => _timer?.Change(150, 2000);
        }
        catch { }   // If the directory cannot be watched, rely on periodic scanning only
    }

    private static string LockName(Operation op) => "op-" + op.Lock;

    private static string Busy(Operation op) => RunningFor(op) is { } j
        ? $"'{j.Op.Title}' is running in {(j.Local ? "this program" : j.Who)} (started {j.Started}); only one can run at a time, wait for it to finish"
        : $"'{op.Title}' is running in another program; wait for it to finish";

    private static void Register(Job job)
    {
        lock (_gate) _local[job.Id] = job;
        Write(job);
        Interlocked.Increment(ref _changes);
    }

    private static void Begin(Job job)
    {
        Register(job);
        Changed?.Invoke(job);
    }

    private static void Finish(Job job, string state, string error)
    {
        lock (_gate) { job.Error = error; job.State = state; job.EndedAt = DateTime.Now; }
        Write(job);
        Interlocked.Increment(ref _changes);
        Changed?.Invoke(job);
    }

    private static void Write(Job j)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var rec = new JobRecord
            {
                Id = j.Id, Op = j.Op.Name, State = j.State, Error = j.Error, Who = j.Who, LogPath = j.LogPath,
                Started = j.StartedAt, Ended = j.Running ? null : DateTime.Now, Pid = j.Pid, ProcessStart = _myStart,
            };
            var f = Path.Combine(Dir, j.Lock + ".json");
            var tmp = $"{f}.{Compat.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec, WlbJson.Default.JobRecord));
            Compat.MoveOver(tmp, f);
        }
        catch { }   // The state file is only for other frontends to see; failing to write it does not affect execution
    }

    private static List<JobRecord> Records()
    {
        List<JobRecord> l = [];
        try
        {
            if (!Directory.Exists(Dir)) return l;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
                try { if (JsonSerializer.Deserialize(File.ReadAllText(f), WlbJson.Default.JobRecord) is { } r) l.Add(r); } catch { }
        }
        catch { }
        return l;
    }

    /// <summary>Job in the state file; one marked running whose process no longer exists is treated as failed.</summary>
    private static Job? FromRecord(JobRecord r)
    {
        if (Operations.Find(r.Op) is not { } op) return null;
        var dead = r.State == "running" && !Alive(r);
        return new Job { Id = r.Id, Op = op, StartedAt = r.Started, EndedAt = r.Ended, Who = r.Who, Pid = r.Pid, State = dead ? "failed" : r.State, Error = dead ? "The program running it has exited" : r.Error, LogPath = r.LogPath };
    }

    private static bool Alive(JobRecord r)
    {
        Process p;
        try { p = Process.GetProcessById(r.Pid); }
        catch { return false; }
        using (p)
        {
            try { return !p.HasExited && Math.Abs(p.StartTime.ToFileTimeUtc() - r.ProcessStart) < 10_000_000; }
            catch { return true; }   // Cannot read the start time of another's process: if the process ID still exists, treat it as alive
        }
    }

    private static void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var now = new Dictionary<string, (string, string)>();
            List<Job> changed = [];
            foreach (var rec in Records())
            {
                if (rec.Pid == Compat.ProcessId || FromRecord(rec) is not { } j) continue;
                now[j.Lock] = (j.Id, j.State);
                if (!_seen.TryGetValue(j.Lock, out var s) || s != (j.Id, j.State)) changed.Add(j);
            }
            _seen = now;
            if (!_scanned) { _scanned = true; return; }   // The first time only records the current state
            foreach (var j in changed)
            {
                Interlocked.Increment(ref _changes);
                Changed?.Invoke(j);
            }
        }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }
}

/// <summary>Reads lines newly added to a log file (to follow a job executing in another process). Position is tracked in bytes, so a multi-byte character split across two reads is not broken.</summary>
public sealed class LogTail(string path)
{
    private long _pos;
    private readonly Decoder _dec = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _partial = new();

    public List<string> ReadNew()
    {
        List<string> lines = [];
        try
        {
            if (!File.Exists(path)) return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _pos) _pos = 0;
            fs.Seek(_pos, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _pos];
            var n = fs.Read(buf, 0, buf.Length);
            var skip = _pos == 0 && n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
            _pos += n;
            var chars = new char[_dec.GetCharCount(buf, skip, n - skip)];
            _dec.GetChars(buf, skip, n - skip, chars, 0);
            foreach (var c in chars)
            {
                if (c == '\n') { lines.Add(_partial.ToString().TrimEnd('\r')); _partial.Clear(); }
                else _partial.Append(c);
            }
        }
        catch { }
        return lines;
    }
}
