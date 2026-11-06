using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace WinLayerBoot.Core;

/// <summary>
/// Environment of one run: settings, output, external commands, mount management, offline hives. Each of the four frontends creates one and hands it to the operations.
/// </summary>
public sealed class Context : IDisposable
{
    public Config Cfg { get; }
    public Output Out { get; }
    public Proc Proc { get; }
    public Mounts Mnt { get; }
    public Hives Hives { get; }
    public Bcd Bcd { get; }
    public CancellationToken Cancel { get; set; } = CancellationToken.None;
    private static int _counter;
    /// <summary>Context number within this process: mount directories and hive names carry it, so several coexisting contexts do not collide.</summary>
    public int Id { get; } = Interlocked.Increment(ref _counter);
    public string LogPath { get; } = Path.Combine(Path.GetTempPath(), $"WLB-{DateTime.Now:yyyyMMdd-HHmmss}-{Compat.ProcessId}.log");

    public Context(Config cfg, IOutput sink, bool detachIsos = true)
    {
        Cfg = cfg;
        Out = new Output(sink);
        Proc = new Proc(Out);
        Mnt = new Mounts(this);
        Hives = new Hives(this);
        Bcd = new Bcd(this);
        Mnt.DetachIsos = detachIsos;
    }

    /// <summary>Throwaway context for UI background queries: output is discarded, the ISO is not unmounted (left until program exit), mount points are removed and hives unloaded after use.</summary>
    public static T Query<T>(Config cfg, Func<Context, T> work)
    {
        using var ctx = new Context(cfg, new NullOutput(), detachIsos: false) { Quick = true };
        return work(ctx);
    }

    /// <summary>Checks while the UI is being edited: very slow checks (computing hashes) are left until actual execution.</summary>
    public bool Quick { get; init; }

    public void Say(string text, Level level = Level.Info) => Out.Say(text, level);
    public void Title(string text) => Out.Title(text);
    public void Stage(string label, string detail = "", bool skipped = false) => Out.Stage(label, detail, skipped);
    public void Result(string key, string value) => Out.Result(key, value);

    /// <summary>Check item "mount directory": a volume mount point can only be created in an NTFS folder.</summary>
    public void CheckMountRoot(CheckList c)
    {
        string fs;
        try { fs = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Cfg.Root))!).DriveFormat; }
        catch { fs = "unreadable"; }
        c.Add("Mount folder", fs == "NTFS", $"{Cfg.MntDir} ({fs}; neither VHDX nor ISO is assigned a drive letter, volumes in use are mounted to folders here)");
    }

    /// <summary>Must run on the work host (Server) itself: the osdevice of {current} is a normal partition, not vhd=.</summary>
    public bool OnWorkHost() => _onWorkHost ??= new BcdStore().Elements(BcdStore.CurrentId).GetValueOrDefault(BcdStore.OsDevice, "").StartsWith("partition=", StringComparison.OrdinalIgnoreCase);
    private static bool? _onWorkHost;   // {current} does not change while the process runs

    /// <summary>End of execution: remove mount points, unmount the ISO, unload hives.</summary>
    public void Cleanup()
    {
        Hives.UnloadAll();
        Mnt.Cleanup();
    }

    public void Dispose() => Cleanup();

    // ---------------------------------------------------------------- Base disk info (cached: the UI recomputes fields on every value change, so the disk cannot be mounted every time)

    public sealed class Intl
    {
        public bool IsServer;
        public string EditionID = "", CurrentBuild = "", UI = "", SysLocale = "", UserLocale = "", Keyboard = "", TimeZone = "";
    }

    // Process-level cache: several contexts in the UI query the same base disk, mounted only once; re-read only if the file changed (modification time differs)
    private static readonly ProbeCache<Intl> _intlCache = new();

    /// <summary>Mounts the base disk read-only, reads the UI language, locale and default input method from the offline registry, then unmounts. Returns null if it cannot be read.</summary>
    public Intl? BaseIntl(string vhdx) => _intlCache.Get(vhdx, () => ReadIntl(vhdx));

    private Intl ReadIntl(string vhdx) => Mnt.WithWindows(vhdx, readOnly: true, "base", (dir, _) =>
        {
            var intl = new Intl();
            ReadHive(dir, "SOFTWARE", soft =>
            {
                using var k = soft.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
                intl.IsServer = (k?.GetValue("InstallationType") as string)?.StartsWith("Server") == true;   // Server or Server Core
                intl.EditionID = k?.GetValue("EditionID") as string ?? "";
                intl.CurrentBuild = k?.GetValue("CurrentBuild") as string ?? "";
            });
            ReadHive(dir, "SYSTEM", sys =>
            {
                using var language = sys.OpenSubKey(@"ControlSet001\Control\Nls\Language");
                using var locale = sys.OpenSubKey(@"ControlSet001\Control\Nls\Locale");
                using var zone = sys.OpenSubKey(@"ControlSet001\Control\TimeZoneInformation");
                intl.UI = LocaleName(language?.GetValue("Default"));
                intl.SysLocale = LocaleName(locale?.GetValue(""));
                intl.TimeZone = zone?.GetValue("TimeZoneKeyName") as string ?? "";
            });
            ReadHive(dir, "DEFAULT", user =>
            {
                using var k = user.OpenSubKey(@"Control Panel\International");
                intl.UserLocale = k?.GetValue("LocaleName") as string ?? "";
                intl.Keyboard = DefaultKeyboard(user);
            });
            return intl;
        }, quiet: true);

    /// <summary>Reads one hive from the base disk. A hive on a read-only disk cannot be loaded directly, so copy it to TEMP and read that.</summary>
    private void ReadHive(string dir, string name, Action<RegistryKey> read)
    {
        var tmpHive = Path.Combine(Path.GetTempPath(), $"WLB_Base{name}-{Compat.ProcessId}-{Id}");
        File.Copy($@"{dir}\Windows\System32\config\{name}", tmpHive, true);
        var hive = $"WLB_Base{name}{Compat.ProcessId}_{Id}";
        if (Hives.RegLoad(hive, tmpHive) == 0)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(hive)) read(k!);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Hives.RegUnload(hive);
        }
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(tmpHive) + "*")) Files.TryDelete(f);
    }

    /// <summary>Converts a hexadecimal LCID in the registry (0804, 00000804) to a language name.</summary>
    private static string LocaleName(object? lcid) =>
        lcid is string s && int.TryParse(s, NumberStyles.HexNumber, null, out var n) ? new CultureInfo(n).Name : "";

    /// <summary>Default input method, written like the unattend file's InputLocale: language:layout (0409:00000409), or language:{program}{profile} when there is an input method program.</summary>
    private static string DefaultKeyboard(RegistryKey user)
    {
        const string keyboardCategory = "{34745C63-B2F0-4784-8B67-5E12C8701A31}", noTip = "{00000000-0000-0000-0000-000000000000}";
        using var preload = user.OpenSubKey(@"Keyboard Layout\Preload");
        if (preload?.GetValue("1") is not string { Length: 8 } layout) return "";
        var lang = layout[4..];
        using var tip = user.OpenSubKey($@"Software\Microsoft\CTF\Assemblies\0x0000{lang}\{keyboardCategory}");
        return tip?.GetValue("Default") is string clsid && clsid != noTip && tip.GetValue("Profile") is string profile
            ? $"{lang}:{clsid}{profile}"
            : $"{lang}:{layout}";
    }

    /// <summary>Version abbreviation: shared by the default base disk name and the default boot menu name.</summary>
    public static string EditionShort(string editionId)
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProfessionalWorkstation"] = "pws", ["Professional"] = "pro", ["ProfessionalEducation"] = "proedu", ["Education"] = "edu",
            ["Core"] = "home", ["CoreSingleLanguage"] = "homesl", ["Enterprise"] = "ent", ["IoTEnterprise"] = "iotent",
            ["ServerStandard"] = "std", ["ServerDatacenter"] = "dc",
        };
        return m.TryGetValue(editionId, out var s) ? s : editionId.ToLowerInvariant();
    }

    /// <summary>Default boot menu name: WLB-&lt;disk name&gt; (&lt;product&gt; &lt;version abbreviation&gt; &lt;language&gt;).</summary>
    public static string DefaultDesc(string name, Intl? info)
    {
        if (info is null) return Config.EntryPrefix + name;
        int.TryParse(info.CurrentBuild, out var b);
        var prod = info.IsServer
            ? b >= 26100 ? "WS2025" : b >= 20348 ? "WS2022" : b >= 17763 ? "WS2019" : "WS"
            : b >= 22000 ? "Win11" : "Win10";
        return $"{Config.EntryPrefix}{name} ({prod} {EditionShort(info.EditionID).ToUpperInvariant()} {info.UI})";
    }

    // ---------------------------------------------------------------- Disks in the vhd directory

    public sealed class Disk
    {
        public string Path = "", Name = "";
        public bool ReadOnly, Attached, IsBase;
        public DateTime Mtime;
        public long FileSize;
        public VhdxInfo? Info;
    }

    /// <summary>All VHDX files in the vhd directory (not mounted).</summary>
    public List<Disk> Disks()
    {
        if (!Directory.Exists(Cfg.VhdDir)) return [];
        return new DirectoryInfo(Cfg.VhdDir).GetFiles("*.vhdx").Select(f => new Disk
        {
            Path = f.FullName,
            Name = System.IO.Path.GetFileNameWithoutExtension(f.Name),
            ReadOnly = f.IsReadOnly,
            Attached = Storage.IsAttached(f.FullName),
            IsBase = f.Name.StartsWith(Config.BasePrefix, StringComparison.OrdinalIgnoreCase),
            Mtime = f.LastWriteTime,
            FileSize = f.Length,
            Info = VhdxInfo.Read(f.FullName),
        }).OrderByDescending(d => d.Mtime).ToList();
    }

    /// <summary>Base disk: WLB-stage1-* and read-only.</summary>
    public List<Disk> BaseDisks() => Disks().Where(d => d.IsBase && d.ReadOnly).ToList();

    /// <summary>Child disk: not a base disk and not mounted.</summary>
    public List<Disk> ChildDisks() => Disks().Where(d => !d.IsBase && !d.Attached).ToList();

    /// <summary>Differencing chain: from a child disk along parent paths up to the base disk (reads file headers, no mounting).</summary>
    public static List<string> Chain(string vhdx)
    {
        List<string> chain = [];
        var cur = vhdx;
        while (cur is not null && chain.Count < 16)
        {
            chain.Add(cur);
            var info = VhdxInfo.Read(cur);
            cur = info is { HasParent: true } && info.ParentPath != "" ? info.ParentPath : null;
            if (cur is not null && !File.Exists(cur)) { chain.Add(cur); break; }
        }
        return chain;
    }
}
