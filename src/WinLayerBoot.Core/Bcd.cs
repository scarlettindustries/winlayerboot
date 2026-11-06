using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinLayerBoot.Core;

/// <summary>
/// BCD: reading, adding (bcdboot), renaming, deleting and hiding boot entries, plus the full verification before and after bcdboot.
/// This tool handles only boot entries whose description starts with WLB-; other boot entries are read-only.
/// </summary>
public sealed class Bcd(Context ctx)
{
    private readonly Context _ctx = ctx;
    private readonly BcdStore _store = new();
    private List<Entry>? _entries;   // Boot entries read in this context; every method that changes the BCD clears it

    public sealed class Entry
    {
        public string Id = "", Desc = "", Vhd = "", VhdRaw = "", Locale = "";
        public bool WinPE, Ems, Hidden, IsDefault, IsBootNext;
        /// <summary>Description starts with WLB-: created by this tool, full functionality.</summary>
        public bool IsWlb => Desc.StartsWith(Config.EntryPrefix, StringComparison.OrdinalIgnoreCase);
        /// <summary>Boots from a VHD (device is vhd=).</summary>
        public bool IsVhd => VhdRaw.StartsWith("vhd=", StringComparison.OrdinalIgnoreCase);
        /// <summary>wlb: full functionality; system: non-VHD system boot entry, can only be set as default / next boot; other: boot entry of another VHD, listed only.</summary>
        public string Tier => IsWlb ? "wlb" : !IsVhd ? "system" : "other";
        public bool VhdExists => Vhd != "" && File.Exists(Vhd);
        /// <summary>Where it points: the VHDX path; for a non-VHD boot entry, the original device text (such as partition=C:).</summary>
        public string Target => Vhd != "" ? Vhd : VhdRaw;

        /// <summary>Status markers, shared by the lists of the four frontends.</summary>
        public List<string> Flags()
        {
            List<string> f = [];
            if (IsVhd && !VhdExists) f.Add("File missing");
            if (Hidden) f.Add("Hidden");
            if (IsDefault) f.Add("Default");
            if (IsBootNext) f.Add("Next boot");
            if (Ems) f.Add("EMS");
            return f;
        }

        /// <summary>A suffix such as " (Hidden, Default)"; empty when there are no markers.</summary>
        public string FlagText => Flags() is { Count: > 0 } f ? $" ({string.Join(", ", f)})" : "";
    }

    public static bool IsWlbDesc(string desc) => desc.StartsWith(Config.EntryPrefix, StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Global lock (cross-process) for changing the system BCD: when several frontends and several people create child disks / snapshots / exports / manage boot entries at the same time, the steps that change the BCD are queued,
    /// especially the whole sequence of taking a snapshot before bcdboot, bcdboot, and restoring items one by one, into which nobody else's change may be inserted. Reentrant on the same thread.
    /// </summary>
    public IDisposable Exclusive() => GlobalLock.Take("bcd", "Modify BCD", m => _ctx.Say(m, Level.Warn));

    /// <summary>Use this when about to change the BCD: clears the boot entries that were read.</summary>
    private BcdStore Write { get { _entries = null; return _store; } }

    private List<string> MenuOrder() => _store.Elements(BootmgrId).GetValueOrDefault(BcdStore.DisplayOrder, "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private void RemoveFromMenu(string id)
    {
        var order = MenuOrder();
        if (order.RemoveAll(x => x.EqualsIgnoreCase(id)) > 0) Write.Set(BootmgrId, BcdStore.DisplayOrder, string.Join("\n", order));
    }

    private void AddToMenu(string id)
    {
        var order = MenuOrder();
        order.RemoveAll(x => x.EqualsIgnoreCase(id));
        order.Add(id);
        Write.Set(BootmgrId, BcdStore.DisplayOrder, string.Join("\n", order));
    }

    private void DefaultToCurrent()
    {
        Write.Set(BootmgrId, BcdStore.DefaultObject, _store.Current());
        _ctx.Say("    It was the default boot entry; the default is switched back to the current system {current}", Level.Warn);
    }

    public const string BootmgrId = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}", FwBootmgrId = "{a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba}";

    /// <summary>All Windows boot loader entries in the BCD (including non-WLB ones; callers filter by IsWlb).</summary>
    public List<Entry> Entries()
    {
        if (_entries is not null) return [.. _entries];
        // Menu order, default entry, next boot
        var bm = _store.Elements(BootmgrId);
        var order = new HashSet<string>(bm.GetValueOrDefault(BcdStore.DisplayOrder, "").Split('\n'), StringComparer.OrdinalIgnoreCase);
        var def = bm.GetValueOrDefault(BcdStore.DefaultObject, "");
        var next = bm.GetValueOrDefault(BcdStore.BootSequence, "").Split('\n')[0];
        var h = new List<Entry>();
        foreach (var id in _store.Objects(BcdStore.OsLoader))
        {
            var o = _store.Elements(id);
            var e = new Entry
            {
                Id = id,
                Desc = o.GetValueOrDefault(BcdStore.Description, ""),
                VhdRaw = o.GetValueOrDefault(BcdStore.Device, ""),
                Locale = o.GetValueOrDefault(BcdStore.Locale, ""),
                WinPE = o.GetValueOrDefault(BcdStore.WinPE) == "True",
                Ems = o.GetValueOrDefault(BcdStore.Ems) == "True",
                Hidden = !order.Contains(id),
                IsDefault = id.EqualsIgnoreCase(def),
                IsBootNext = id.EqualsIgnoreCase(next),
            };
            var v = Regex.Match(e.VhdRaw, @"vhd=\[([A-Z]:)\](\\[^,]+)", RegexOptions.IgnoreCase);
            if (v.Success) e.Vhd = ActualCase(v.Groups[1].Value + v.Groups[2].Value);
            if (!e.WinPE) h.Add(e);
        }
        _entries = h;
        return [.. _entries];
    }

    public List<Entry> WlbEntries() => Entries().Where(e => e.IsWlb).ToList();

    /// <summary>Those that can be set as default / next boot: WLB boot entries + non-VHD system boot entries.</summary>
    public List<Entry> BootTargets() => Entries().Where(e => e.Tier != "other").ToList();

    public Entry? FindTarget(string idOrDesc) =>
        BootTargets().FirstOrDefault(e => e.Id.EqualsIgnoreCase(idOrDesc) || e.Desc.EqualsIgnoreCase(idOrDesc));

    /// <summary>Paths read from the BCD are uppercase; change them to the actual case of the file name on disk.</summary>
    private static string ActualCase(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return path;
            return Directory.GetFiles(dir, Path.GetFileName(path)).FirstOrDefault() ?? path;
        }
        catch { return path; }
    }

    public static string VhdDevice(string file) => Regex.Replace(file, "^([A-Za-z]):", "vhd=[$1:]");

    public Entry? Find(string idOrDesc) =>
        WlbEntries().FirstOrDefault(e => e.Id.EqualsIgnoreCase(idOrDesc) || e.Desc.EqualsIgnoreCase(idOrDesc));

    /// <summary>The WLB boot entries that point to this VHDX.</summary>
    public Entry? ForVhd(string vhdx) =>
        WlbEntries().FirstOrDefault(e => e.Vhd.EqualsIgnoreCase(vhdx));


    /// <summary>
    /// Boot menu language: BootLocale in the settings. If winDir is given, check whether the boot files shipped with that Windows have this language; if not, fall back to en-US.
    /// </summary>
    public string MenuLocale(string? winDir = null)
    {
        var l = _ctx.Cfg.BootLocale;
        if (winDir is null || l.EqualsIgnoreCase("en-US") || Directory.Exists($@"{winDir}\Windows\Boot\EFI\{l}")) return l;
        _ctx.Say($"    the boot files in {winDir} have no {l}; using en-US for the boot menu language", Level.Warn);
        return "en-US";
    }

    /// <summary>
    /// Uses bcdboot to add a boot entry for a mounted Windows directory, changes it to point at the VHDX and sets the description; every other setting bcdboot changed along the way is restored,
    /// and the boot menu language ({bootmgr} and the new boot entry) is set to BootLocale from the settings. Returns the Id of the new boot entry.
    /// </summary>
    public string AddEntryWithBcdboot(string winDir, string vhdx, string desc, bool ems)
    {
        using var bcd = Exclusive();
        var locale = MenuLocale(winDir);
        _entries = null;
        var before = _store.Objects(BcdStore.OsLoader);
        var snap = Snapshot();
        string? id = null;
        try
        {
            _ctx.Proc.Exec("bcdboot", "successfully|Failure", "bcdboot.exe", $@"{winDir}\Windows", "/l", locale, "/d", "/addlast");
            var ids = _store.Objects(BcdStore.OsLoader).Except(before, StringComparer.OrdinalIgnoreCase)
                .Where(x => _store.Elements(x).GetValueOrDefault(BcdStore.WinPE) != "True").ToList();
            if (ids.Count != 1) throw new WlbError($"found {ids.Count} new boot entries, expected 1: {string.Join(" ", ids)}");
            id = ids[0];
            Write.SetVhd(id, vhdx);
            _store.Set(id, BcdStore.Description, desc);
            if (ems) _store.Set(id, BcdStore.Ems, "True");
            _ctx.Say($"    New boot entry {id} → {VhdDevice(vhdx)}, boot menu language {locale}");
            return id;
        }
        finally
        {
            _ctx.Say("  Verifying BCD settings changed by bcdboot as a side effect");
            try { Restore(snap, id is null ? [] : [id], menuLocale: locale); }
            catch (Exception e) { _ctx.Say($"  BCD verification failed: {e.Message}; compare with bcdedit /enum all /v and fix manually", Level.Error); }
        }
    }

    /// <summary>Mounts the VHDX (read-write), adds a boot entry for it, unmounts.</summary>
    public string AddEntryForVhd(string vhdx, string desc, bool ems) =>
        _ctx.Mnt.WithWindows(vhdx, readOnly: false, "win", (win, _) => AddEntryWithBcdboot(win, vhdx, desc, ems));

    /// <summary>Copies a boot entry for use by another VHDX (snapshot).</summary>
    public string CopyEntry(string fromId, string vhdx, string desc)
    {
        using var bcd = Exclusive();
        var id = Write.Copy(fromId);
        _store.Set(id, BcdStore.Description, desc);
        _store.SetVhd(id, vhdx);
        if (_store.Elements(id).ContainsKey(BcdStore.ResumeObject)) _store.Delete(id, BcdStore.ResumeObject);   // The hibernate resume entry that was copied belongs to the original disk, and native VHD boot does not support hibernation either
        AddToMenu(id);
        var locale = MenuLocale();
        _store.Set(id, BcdStore.Locale, locale);
        _store.Set(BootmgrId, BcdStore.Locale, locale);
        _ctx.Say($"    Copied {fromId} → {id}, boot menu language {locale}");
        return id;
    }

    public void Rename(string id, string desc)
    {
        using var bcd = Exclusive();
        Write.Set(id, BcdStore.Description, desc);
    }

    public void Delete(string id)
    {
        using var bcd = Exclusive();
        // If it is the bootsequence, clear that first; if it is the default boot entry, first set the default back to the current system, otherwise there is no default entry at the next boot; references in the menu order must also be removed
        var e = Entries().FirstOrDefault(x => x.Id.EqualsIgnoreCase(id));
        if (e is { IsBootNext: true }) Write.Delete(BootmgrId, BcdStore.BootSequence);
        if (e is { IsDefault: true }) DefaultToCurrent();
        RemoveFromMenu(id);
        Write.DeleteObject(id);
    }

    /// <summary>Sets it as the default boot entry (booted after the menu timeout; unlike bootsequence, it is long-lived).</summary>
    public void SetDefault(string id)
    {
        using var bcd = Exclusive();
        Write.Set(BootmgrId, BcdStore.DefaultObject, id);
    }

    public void Hide(string id)
    {
        using var bcd = Exclusive();
        var e = Entries().FirstOrDefault(x => x.Id.EqualsIgnoreCase(id));
        if (e is { IsDefault: true }) DefaultToCurrent();
        RemoveFromMenu(id);
        if (e is { IsBootNext: true }) Write.Delete(BootmgrId, BcdStore.BootSequence);
    }

    public void Show(string id)
    {
        using var bcd = Exclusive();
        AddToMenu(id);
    }

    public void BootNext(string id)
    {
        using var bcd = Exclusive();
        Write.Set(BootmgrId, BcdStore.BootSequence, id);
    }


    // ---------------------------------------------------------------- full verification

    /// <summary>All objects of the system BCD: object -> element type number -> value.</summary>
    public Dictionary<string, Dictionary<uint, string>> Snapshot() =>
        _store.Objects(BcdStore.AllObjects).ToDictionary(id => id, _store.Elements, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<uint> Simple =
    [
        BcdStore.Description, BcdStore.Locale, BcdStore.BootEms, BcdStore.BootShutdownDisabled, BcdStore.ResumeObject, BcdStore.BootMgrResume,
        BcdStore.Timeout, BcdStore.Ems, BcdStore.BootMenuPolicy, BcdStore.Nx, BcdStore.DisplayBootMenu,
    ];

    /// <summary>
    /// bcdboot also changes other settings in the system BCD along the way: bootems / locale / resumeobject / bootshutdowndisabled / menu order of {bootmgr},
    /// the firmware boot order of {fwbootmgr}.
    /// Here the snapshot taken before bcdboot is compared object by object and element by element: the allowed changes are newly created objects (newIds and their hibernate resume entries),
    /// the {bootmgr} menu order with newIds added and hiddenId removed; single-value elements are written back with their original value, the menu order is written back in its original order, and anything else that cannot be restored is listed.
    /// bcdedit /import is not used to restore everything: on UEFI machines it rewrites the firmware boot entries and firmware boot order.
    /// When menuLocale is given, the locale of {bootmgr} is not restored but set to it (the boot menu language is decided by the settings).
    /// </summary>
    public bool Restore(Dictionary<string, Dictionary<uint, string>> before, string[] newIds, string? hiddenId = null, string? menuLocale = null)
    {
        _entries = null;
        var after = Snapshot();
        var expectedNew = new HashSet<string>(newIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in newIds)
            if (after.TryGetValue(id, out var o) && o.TryGetValue(BcdStore.ResumeObject, out var r)) expectedNew.Add(r);
        static string Name(string id, Dictionary<uint, string> o) => o.GetValueOrDefault(BcdStore.Description) is { } d ? $"{id} ({d})" : id;
        static List<string> Ids(string? v) => (v ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        List<string> left = [];
        var fixedCount = 0;
        foreach (var (id, a) in after)
        {
            if (!before.TryGetValue(id, out var b))
            {
                if (!expectedNew.Contains(id)) left.Add($"new object {Name(id, a)}");
                continue;
            }
            foreach (var key in b.Keys.Union(a.Keys).ToList())
            {
                b.TryGetValue(key, out var bv);
                a.TryGetValue(key, out var av);
                var keyName = BcdStore.ElementName(key);
                if (menuLocale is not null && id.EqualsIgnoreCase(BootmgrId) && key == BcdStore.Locale) bv = menuLocale;
                if (key == BcdStore.DisplayOrder && (id.EqualsIgnoreCase(BootmgrId) || id.EqualsIgnoreCase(FwBootmgrId)))
                {
                    var want = Ids(bv).Where(x => !x.EqualsIgnoreCase(hiddenId)).ToList();
                    if (id.EqualsIgnoreCase(BootmgrId)) want.AddRange(newIds.Where(n => !want.Contains(n, StringComparer.OrdinalIgnoreCase)));
                    if (Ids(av).SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) continue;
                    _store.Set(id, key, string.Join("\n", want));
                    _ctx.Say($"    Restored display order of {Name(id, b)}");
                    fixedCount++;
                    continue;
                }
                if (bv == av) continue;
                if (Simple.Contains(key))
                {
                    if (bv is null) _store.Delete(id, key);
                    else _store.Set(id, key, bv);
                    _ctx.Say($"    Restored {keyName} of {Name(id, b)}: {av ?? "(none)"} → {bv ?? "(deleted)"}");
                    fixedCount++;
                }
                else left.Add($"{keyName} of {Name(id, b)}: {bv ?? "(none)"} → {av ?? "(none)"}");
            }
        }
        foreach (var id in before.Keys.Where(k => !after.ContainsKey(k))) left.Add($"object {Name(id, before[id])} disappeared");
        if (left.Count == 0)
        {
            _ctx.Say($"  BCD check: {(newIds.Length > 0 ? "apart from the new boot entries, " : "")}identical to before bcdboot{(fixedCount > 0 ? $" (restored {fixedCount} change(s))" : ", no changes")}");
            return true;
        }
        _ctx.Say($"  Warning: {left.Count} more change(s) in BCD could not be restored automatically:", Level.Warn);
        foreach (var x in left) _ctx.Say($"    {x}", Level.Warn);
        return false;
    }
}
