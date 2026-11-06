using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace WinLayerBoot.Core;

/// <summary>
/// Mount management: neither VHDX nor ISO gets a drive letter. Volumes handed to external tools (dism, bcdboot, reg load)
/// are mounted at &lt;Root&gt;\mnt\&lt;start time&gt;-&lt;pid&gt;\&lt;purpose&gt; and removed after use; mount points left by a process that exited unexpectedly are cleaned up on the next run.
/// </summary>
public sealed class Mounts(Context ctx)
{
    private readonly Context _ctx = ctx;
    // The directory name carries the context number: several contexts in one process (UI queries + background execution) are in use at the same time and must not collide
    private readonly string _runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Compat.ProcessId}-{ctx.Id}";
    private readonly List<string> _dirs = [];
    private readonly Dictionary<string, string> _isoDirs = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan VolumeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether to detach the ISOs this process mounted at exit. The command line does one thing at a time and detaches when done;
    /// in Web / TUI / window, queries and execution use the same ISO at the same time, so nobody may detach midway; all are detached together at program exit (IsoPool.DetachAll).
    /// </summary>
    public bool DetachIsos { get; set; } = true;

    private string MntRoot => Path.Combine(_ctx.Cfg.MntDir, _runId);

    /// <summary>Mounts the volume at mnt\&lt;this run&gt;\&lt;name&gt; and returns the folder path. If the name is taken, a sequence number is added.</summary>
    public string MountVolume(string name, string volumeName)
    {
        var dir = Path.Combine(MntRoot, name);
        for (var i = 2; _dirs.Contains(dir, StringComparer.OrdinalIgnoreCase); i++) dir = Path.Combine(MntRoot, $"{name}{i}");
        Storage.MountAt(dir, volumeName);
        _dirs.Add(dir);
        return dir;
    }

    public void UnmountDir(string? dir)
    {
        if (dir is null || !_dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
        Storage.UnmountAt(dir);
        _dirs.RemoveAll(d => d.EqualsIgnoreCase(dir));
    }

    /// <summary>Mounts the VHDX (no drive letter) and returns the partition table once the Windows partition and the ESP (if any) both have volumes. The wait relies on volume arrival notifications.</summary>
    public List<Storage.Part> AttachVhd(string vhdx, bool readOnly, bool quiet = false)
    {
        using var w = new Storage.VolumeWatcher();
        Storage.Attach(vhdx, readOnly);
        List<Storage.Part> parts = [];
        var t = w.Until(() =>
            {
                parts = Storage.PartitionsOf(vhdx);
                return parts.Any(p => p.Type == "Basic" && p.VolumeName != "") && parts.Where(p => p.Type == "System").All(p => p.VolumeName != "");
            },
            VolumeTimeout, $"volume of {Path.GetFileName(vhdx)}");
        if (!quiet) _ctx.Say($"    Mounted {Path.GetFileName(vhdx)} ({(readOnly ? "read-only" : "read-write")}, no drive letter), volume ready in {t.TotalMilliseconds:0} ms");
        return parts;
    }

    /// <summary>
    /// Mounts the VHDX, mounts the Windows partition at a folder (name) under mnt, runs body(folder, partition table); when done, removes the mount point and detaches (also on error).
    /// It is fine for body to detach first itself (for example to print "detached" as a step). Disks mounted by others are left alone.
    /// </summary>
    public T WithWindows<T>(string vhdx, bool readOnly, string name, Func<string, List<Storage.Part>, T> body, bool quiet = false)
    {
        if (Storage.IsAttached(vhdx)) throw new WlbError($"{vhdx} is already mounted");
        string? dir = null;
        try
        {
            var parts = AttachVhd(vhdx, readOnly, quiet);
            dir = MountVolume(name, WindowsPart(parts, vhdx).VolumeName);
            return body(dir, parts);
        }
        finally
        {
            try { UnmountDir(dir); if (Storage.IsAttached(vhdx)) DetachVhd(vhdx); }
            catch (Exception e) { _ctx.Say($"  Failed to unmount {Path.GetFileName(vhdx)}: {e.Message}", Level.Warn); }
        }
    }

    /// <summary>The largest Basic partition (the Windows partition).</summary>
    public Storage.Part WindowsPart(List<Storage.Part> parts, string vhdx) =>
        parts.Where(p => p.Type == "Basic").OrderByDescending(p => p.Size).FirstOrDefault() ?? throw new WlbError($"No Windows partition in {vhdx}");

    /// <summary>Removes the folders mounted on this disk's volumes first, then detaches.</summary>
    public void DetachVhd(string vhdx)
    {
        var vols = Storage.PartitionsOf(vhdx).Select(p => p.VolumeName).Where(v => v != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _dirs.ToArray())
            if (vols.Contains(Storage.VolumeAt(d) ?? "")) UnmountDir(d);
        Storage.Detach(vhdx);
    }

    /// <summary>Mounts the ISO without a drive letter (uses it directly if already mounted) and mounts the volume at a folder; the same ISO is mounted only once per run; at exit only the ones this process mounted are detached.</summary>
    public string MountIso(string path, string name)
    {
        if (_isoDirs.TryGetValue(path, out var have) && Directory.Exists(have)) return have;
        var img = Storage.DiskImage(path) ?? throw new WlbError($"{path} does not exist");
        Storage.Vol? vol = null;
        TimeSpan t;
        using (var w = new Storage.VolumeWatcher())
        {
            if (!img.Attached) IsoPool.Attach(path);
            t = w.Until(() => (vol = Storage.VolumeOfImage(path)) is not null, VolumeTimeout, $"volume of {Path.GetFileName(path)}");
        }
        var dir = MountVolume(name, vol!.UniqueId);
        _isoDirs[path] = dir;
        _ctx.Say($"    {path} → {dir} (volume ready in {t.TotalMilliseconds:0} ms)");
        return dir;
    }

    /// <summary>At exit: removes this run's mount points, detaches the ISOs it mounted itself, and deletes the empty mount directory.</summary>
    public void Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_dirs.ToArray()))
        {
            try { UnmountDir(d); }
            catch (Exception e) { _ctx.Say($"  Failed to remove mount point {d}: {e.Message}", Level.Warn); }
        }
        _isoDirs.Clear();
        if (DetachIsos) IsoPool.DetachAll(_ctx.Say);
        RemoveEmptyDir(MntRoot);
        RemoveEmptyDir(_ctx.Cfg.MntDir);
    }

    private static void RemoveEmptyDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Storage.IsMountPoint(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch { }
    }

    /// <summary>At startup, cleans up mount points left by earlier abnormal exits (only run directories whose process is gone).</summary>
    public void CleanStale()
    {
        var mnt = _ctx.Cfg.MntDir;
        if (!Directory.Exists(mnt)) return;
        foreach (var run in Directory.GetDirectories(mnt))
        {
            var m = Regex.Match(Path.GetFileName(run), @"-(\d+)(?:-\d+)?$");
            if (m.Success && ProcessAlive(int.Parse(m.Groups[1].Value))) continue;
            foreach (var d in Directory.GetDirectories(run))
            {
                try
                {
                    if (Storage.IsMountPoint(d)) { Storage.UnmountAt(d); _ctx.Say($"  Cleaned up mount point left over from last time {d}", Level.Warn); }
                    else RemoveEmptyDir(d);
                }
                catch (Exception e) { _ctx.Say($"  Failed to clean up {d}: {e.Message}", Level.Warn); }
            }
            RemoveEmptyDir(run);
        }
        RemoveEmptyDir(mnt);
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}

/// <summary>
/// Process-level cache of read-only survey results (base disk language, child disk partitions and drivers, display settings): the UI recomputes fields on every value change, so the disk cannot be mounted each time.
/// Cached by file path and modification time, re-read only when the file changed; the same file is surveyed by only one context at a time while the others wait for the result; a disk that is currently mounted is not surveyed (returns null).
/// </summary>
public sealed class ProbeCache<T>(bool cacheNull = false, Func<T, bool>? valid = null) where T : class
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Mtime, T? Value)> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _gates = new(StringComparer.OrdinalIgnoreCase);

    public T? Get(string path, Func<T?> load)
    {
        if (!File.Exists(path)) return null;
        var mt = File.GetLastWriteTimeUtc(path);
        if (Hit(path, mt, out var v)) return v;
        lock (_gates.GetOrAdd(path, _ => new object()))
        {
            if (Hit(path, mt, out v)) return v;
            if (Storage.IsAttached(path)) return null;
            try { v = load(); } catch { v = null; }
            if (v is not null || cacheNull) _map[path] = (mt, v);
            return v;
        }
    }

    private bool Hit(string path, DateTime mt, out T? v)
    {
        v = null;
        if (!_map.TryGetValue(path, out var c) || c.Mtime != mt) return false;
        if (c.Value is null) return cacheNull;
        if (valid is not null && !valid(c.Value)) return false;
        v = c.Value;
        return true;
    }

    public void Forget(string path) => _map.TryRemove(path, out _);
}

/// <summary>ISOs mounted by this process: registered on mount, detached together at program exit (or when the command line finishes).</summary>
public static class IsoPool
{
    private static readonly List<string> _isos = [];
    private static readonly object _gate = new();

    public static void Attach(string path)
    {
        lock (_gate)
        {
            if (_isos.Contains(path, StringComparer.OrdinalIgnoreCase) || Storage.IsAttached(path)) return;
            Storage.Attach(path, readOnly: true);
            _isos.Add(path);
        }
    }

    public static void DetachAll(Action<string, Level>? say = null)
    {
        lock (_gate)
        {
            // Another wlb process may be using the same ISO (when it mounted, it found it already mounted and did not register it): if other wlb processes are still running, do not detach
            if (_isos.Count > 0 && Process.GetProcessesByName("wlb").Length + Process.GetProcessesByName("wlb-ui").Length > 1)
            {
                say?.Invoke("  Other wlb instances are still running, leaving the ISO for them", Level.Dim);
                _isos.Clear();
                return;
            }
            foreach (var p in _isos.ToArray())
            {
                try { Storage.Detach(p); say?.Invoke($"  Unmounted {p}", Level.Dim); }
                catch (Exception e) { say?.Invoke($"  Failed to unmount {p}: {e.Message}", Level.Warn); }
            }
            _isos.Clear();
        }
    }
}

/// <summary>Offline registry: load and unload hives and write values. The hive name carries the pid and context number, so several frontends and contexts running at once do not collide.</summary>
public sealed class Hives(Context ctx)
{
    private readonly Context _ctx = ctx;
    private readonly List<string> _loaded = [];

    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadKeyW(IntPtr key, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKeyW(IntPtr key, string subKey);

    /// <summary>Loads a hive file at HKLM\name and returns the Win32 error code (0 = success).</summary>
    public static int RegLoad(string name, string file)
    {
        Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
        return RegLoadKeyW(HkeyLocalMachine, name, file);
    }

    /// <summary>Unloads HKLM\name and returns the Win32 error code (0 = success).</summary>
    public static int RegUnload(string name)
    {
        Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege");
        return RegUnLoadKeyW(HkeyLocalMachine, name);
    }

    /// <summary>Loads a hive and returns its name under HKLM (for example WLB_Sys1234_2).</summary>
    public string Load(string tag, string file)
    {
        var name = $"WLB_{tag}{Compat.ProcessId}_{_ctx.Id}";
        if (_loaded.Contains(name)) return name;
        var r = RegLoad(name, file);
        if (r != 0) throw new WlbError($"Failed to load hive {tag} ({file}): {new Win32Exception(r).Message}");
        _loaded.Add(name);
        return name;
    }

    public void UnloadAll()
    {
        foreach (var h in _loaded.ToArray())
        {
            var code = 1;
            for (var i = 0; i < 5 && code != 0; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                code = RegUnload(h);
                if (code != 0) System.Threading.Thread.Sleep(1000);
            }
            if (code != 0) _ctx.Say($@"  Warning: failed to unload hive {h}, run reg unload HKLM\{h} manually", Level.Warn);
        }
        _loaded.Clear();
    }

    /// <summary>Equivalent to reg add ... /t REG_DWORD; the path is relative to HKLM, and missing intermediate keys are created.</summary>
    public static void Dword(string what, string path, string name, int value)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path, true);
            k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception e) { throw new WlbError($"{what} failed: {e.Message}"); }
    }

    public static void String(string what, string path, string name, string value)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path, true);
            k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception e) { throw new WlbError($"{what} failed: {e.Message}"); }
    }

    public static bool KeyExists(string path)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        return k is not null;
    }
}
