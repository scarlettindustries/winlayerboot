using System;
using System.Threading;

namespace WinLayerBoot.Core;

/// <summary>
/// Cross-process named lock (Global\ mutex, shared by all sessions and all users): the CLI, TUI, window and web may be open several at a time and used by several people at once,
/// so changing the BCD, writing preset files and running the same menu item all have to queue. A mutex is bound to its thread: release it (using) on the thread that acquired it.
/// A lock left by a process that exited abnormally (abandoned) is taken over directly; the same thread can re-enter.
/// </summary>
public static class GlobalLock
{
    private static Mutex Open(string name)
    {
        var n = "WinLayerBoot-" + name.Replace('\\', '_');
        // Without SeCreateGlobalPrivilege (not an administrator) a Global\ mutex cannot be created: fall back to this session, since execution needs administrator anyway
        try { return new Mutex(false, @"Global\" + n); }
        catch (UnauthorizedAccessException) { return new Mutex(false, @"Local\" + n); }
    }

    /// <summary>Returns null immediately if it cannot be acquired.</summary>
    public static IDisposable? TryTake(string name)
    {
        var m = Open(name);
        try { if (m.WaitOne(0)) return new Held(m); }
        catch (AbandonedMutexException) { return new Held(m); }
        m.Dispose();
        return null;
    }

    /// <summary>Waits until acquired; says so via waiting when it has to wait. Throws WlbError on timeout.</summary>
    public static IDisposable Take(string name, string what, Action<string>? waiting = null, TimeSpan? timeout = null)
    {
        var m = Open(name);
        var limit = timeout ?? TimeSpan.FromMinutes(10);
        var until = DateTime.Now + limit;
        var said = false;
        try
        {
            while (true)
            {
                if (m.WaitOne(said ? 1000 : 0)) return new Held(m);
                if (!said) { waiting?.Invoke($"    {what}: in use by another program, waiting for it to finish..."); said = true; }
                if (DateTime.Now > until) throw new WlbError($"{what}: waited {limit.TotalMinutes:0} minutes, still held by another program");
            }
        }
        catch (AbandonedMutexException) { return new Held(m); }
        catch { m.Dispose(); throw; }
    }

    private sealed class Held(Mutex m) : IDisposable
    {
        private Mutex? _m = m;

        public void Dispose()
        {
            var x = Interlocked.Exchange(ref _m, null);
            if (x is null) return;
            try { x.ReleaseMutex(); } catch { }
            x.Dispose();
        }
    }
}
