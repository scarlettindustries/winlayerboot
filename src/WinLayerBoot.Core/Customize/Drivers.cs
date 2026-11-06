using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;

namespace WinLayerBoot.Customize;

/// <summary>
/// virtio drivers: needed when a child disk boots in a VM (whole disk via virtio-scsi, virtio NIC). Offline-injected storage drivers are set to load at boot;
/// real machines have no virtio devices, so these drivers are not used there.
/// </summary>
public sealed class VirtioDrivers : Customization
{
    public static readonly string[] Names = ["vioscsi", "viostor", "NetKVM", "Balloon", "vioserial", "viorng", "fwcfg", "pvpanic", "vioinput"];

    public override string Key => "Virtio";
    public override string Label => "Inject virtio drivers";
    public override string Group => "Drivers";
    public override bool DefaultOn => false;
    public override string Help => "vioscsi, viostor, NetKVM, Balloon, vioserial, viorng, fwcfg, pvpanic, vioinput; Win11 uses w11\\amd64, Server uses 2k25\\amd64";

    public static bool HasInf(string dir) => Directory.Exists(dir) && Directory.GetFiles(dir, "*.inf").Length > 0;

    public override void Check(LayerPlan p, Values v, CheckList c)
    {
        var dir = p.VirtioDir;
        c.Add("virtio ISO", dir != "", dir != "" ? p.Cfg.ResolveVirtioIso() : $"Not found (VirtioIso in Settings or {p.Cfg.IsoDir}\\*virtio*.iso)");
        var miss = Names.Where(d => !(dir != "" && HasInf($@"{dir}\{d}\{p.VioOs}\amd64"))).ToList();
        c.Add($"virtio drivers ({p.VioOs})", dir != "" && miss.Count == 0, miss.Count > 0 ? $"Missing from ISO: {string.Join(", ", miss)}" : string.Join(", ", Names));
    }

    public override void Apply(LayerRun r)
    {
        // Take only the directory for the matching OS (w11 or 2k25) for each driver; do not recurse the whole ISO, or drivers for other OS versions get installed
        Inject(r.Ctx, r.Win, r.Plan.VirtioDir, r.Plan.VioOs);
    }

    public static void Inject(Context ctx, string win, string vioDir, string vioOs)
    {
        ctx.Say($"    Injecting virtio drivers ({vioOs}): {string.Join(", ", Names)}");
        using (var dism = new DismImage(win, ctx.Out))
            foreach (var d in Names)
                foreach (var inf in Directory.GetFiles($@"{vioDir}\{d}\{vioOs}\amd64", "*.inf")) dism.AddDriver(inf);
        ctx.Say($"    Injected {Names.Length}");
    }
}

/// <summary>Real-machine drivers: injects all INF files under &lt;Root&gt;\drivers\&lt;machine name&gt;.</summary>
public sealed class MachineDrivers : Customization
{
    public override string Key => "Drivers";
    public override string Label => "Inject physical machine drivers";
    public override string Group => "Drivers";
    public override bool DefaultOn => false;
    public override string Help => "Inject all INF files (recursive) under drivers\\<machine name>";

    public override IEnumerable<Field> ExtraFields(LayerPlan p, Values v)
    {
        var known = Directory.Exists(p.Cfg.DriversDir) ? Directory.GetDirectories(p.Cfg.DriversDir).Select(Path.GetFileName).Select(x => x!).ToArray() : [];
        yield return new Field("Machine", "Machine name", FieldKind.Choice, known.FirstOrDefault() ?? "")
        {
            Group = Group, Choices = known,
            Help = known.Length > 0 ? $"Existing in {p.Cfg.DriversDir}: {string.Join(", ", known)}" : $"No driver folders in {p.Cfg.DriversDir} yet",
        };
    }

    public string Dir(LayerPlan p, Values v) => Path.Combine(p.Cfg.DriversDir, v.Get("Machine"));

    public override void Check(LayerPlan p, Values v, CheckList c)
    {
        var m = v.Get("Machine");
        c.Add("Machine name", Regex.IsMatch(m, "^[A-Za-z0-9_-]+$"), m == "" ? "Not set" : m);
        var dir = Dir(p, v);
        var n = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories).Length : 0;
        c.Add("Physical machine driver folder", n > 0, $"{dir} ({n} INF files)");
    }

    public override string SummaryName(Values v) => $"Drivers {v.Get("Machine")}";

    public override void Apply(LayerRun r)
    {
        var dir = Dir(r.Plan, r.V);
        r.Say($"    Physical machine drivers ({r.V.Get("Machine")}): {dir}");
        var infs = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories);
        var failed = 0;
        r.Dism(dism =>
        {
            // One failure does not affect the others (the directory may hold INF files that are not drivers, such as autorun.inf); try them all, then report the error
            foreach (var inf in infs)
            {
                try { dism.AddDriver(inf); }
                catch (WlbError e) { failed++; r.Say($"    {e.Message}", Level.Warn); }
            }
        });
        if (failed > 0) throw new WlbError($"{failed} of {infs.Length} INF files failed to inject");
        r.Say($"    Injected {infs.Length} INF files");
    }
}

/// <summary>Restores display settings: &lt;Root&gt;\display\&lt;configuration name&gt;\display.reg (HKLM display configuration + HKCU per-display scaling).</summary>
public sealed class DisplayRestore : Customization
{
    public override string Key => "Display";
    public override string Label => "Restore display settings";
    public override string Group => "Display";
    public override bool DefaultOn => false;
    public override string Help => "Layout, resolution, refresh rate, rotation, primary display, HDR, scaling; other detection orders of the same set of displays are unified to the most recently used one";

    public override IEnumerable<Field> ExtraFields(LayerPlan p, Values v)
    {
        var known = Directory.Exists(p.Cfg.DisplayDir)
            ? Directory.GetDirectories(p.Cfg.DisplayDir).Where(d => File.Exists(Path.Combine(d, "display.reg"))).Select(Path.GetFileName).Select(x => x!).ToArray()
            : [];
        yield return new Field("DisplayName", "Display profile name", FieldKind.Choice, known.LastOrDefault() ?? "")
        {
            Group = Group, Choices = known,
            Help = known.Length > 0 ? $"Existing in {p.Cfg.DisplayDir}: {string.Join(", ", known)}" : $"None in {p.Cfg.DisplayDir} yet (create with \"Save display settings\")",
        };
    }

    public string File_(LayerPlan p, Values v) => Path.Combine(p.Cfg.DisplayDir, v.Get("DisplayName"), "display.reg");

    public override void Check(LayerPlan p, Values v, CheckList c) => c.Add("Display profile", File.Exists(File_(p, v)), File_(p, v));

    public override string SummaryName(Values v) => $"Display {v.Get("DisplayName")}";

    public override void Apply(LayerRun r)
    {
        // The HKLM part is written into the child disk's SYSTEM, the HKCU part into the default user (picked up when a new user first logs in)
        var file = File_(r.Plan, r.V);
        r.Say($"    Restore display settings: {r.V.Get("DisplayName")}");
        var (text, fixedList) = DisplayReg.Normalize(File.ReadAllText(file, Encoding.Unicode));
        if (fixedList.Count > 0) r.Say($"    Other detection orders of the same set of displays changed to the most recently used layout: {string.Join(", ", fixedList)}");
        var sys = r.Sys();
        var def = r.DefUser();
        text = Regex.Replace(text, Regex.Escape(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet"), $@"HKEY_LOCAL_MACHINE\{sys}\ControlSet001".Replace("$", "$$"), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, Regex.Escape("HKEY_CURRENT_USER"), $@"HKEY_LOCAL_MACHINE\{def}".Replace("$", "$$"), RegexOptions.IgnoreCase);
        try { RegFile.Import(text); }
        finally { r.Unload(); }
    }
}
