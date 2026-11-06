using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;
using WinLayerBoot.Customize;

namespace WinLayerBoot.Ops;

/// <summary>Saves the display settings from a child disk to display\&lt;config name&gt;\display.reg.</summary>
public sealed class DisplaySaveOp : Operation
{
    public override string Name => "display";
    public override string Title => "Save display settings";
    public override string Summary => "Mount a child disk read-only and export the display layout, resolution, refresh rate, HDR and per-display scaling, which can be restored when creating child disks";
    public override string[] Steps =>
    [
        "Mount the child disk read-only, copy out SYSTEM and the users' NTUSER, then unmount right away",
        "Export Configuration, Connectivity, MonitorDataStore and ScaleFactors under GraphicsDrivers plus the user's PerMonitorSettings, merged into display.reg",
        "README.md lists the most recently used layout with all displays connected",
    ];
    public override string[] Stages => ["Export registry", "Write README.md"];
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["Child disk exists"] = "Source", ["Not a base disk"] = "Source", ["Child disk not mounted"] = "Source", ["Registry read"] = "Source", ["Has display profile"] = "Source",
        ["User exists"] = "User", ["Profile name"] = "Name", ["Profile name available"] = "Name",
    };
    public override bool Mutates => false;

    private sealed class Copied
    {
        public string Tmp = "";
        public List<string> Users = [];
        public List<string[]> Layout = [];
    }

    private static readonly ProbeCache<Copied> _cache = new(cacheNull: true, valid: c => Directory.Exists(c.Tmp));

    /// <summary>Mounts the child disk read-only once: copies SYSTEM and each user's ntuser* to a temp directory, detaches immediately, and reads the copies from then on. Returns null on failure. Cached.</summary>
    private static Copied? Copy(Context ctx, string vhdx) => _cache.Get(vhdx, () =>
    {
        ctx.Say("  Mounting the child disk read-only, copying the registry...", Level.Dim);
        var tmp = Path.Combine(Path.GetTempPath(), $"wlb-display-{Guid.NewGuid():N}");
        var r = ctx.Mnt.WithWindows(vhdx, readOnly: true, "src", (dir, _) =>
        {
            Directory.CreateDirectory(Path.Combine(tmp, "sys"));
            foreach (var f in Directory.GetFiles($@"{dir}\Windows\System32\config", "SYSTEM*"))
                File.Copy(f, Path.Combine(tmp, "sys", Path.GetFileName(f)), true);
            // All of a user's ntuser* files must be copied (including .TM.blf and .regtrans-ms); with only .LOG1/.LOG2 copied, reg load reports "file name too long"
            List<string> users = [];
            string[] skip = ["Default", "Default User", "Public", "All Users", "defaultuser0"];
            foreach (var u in new DirectoryInfo($@"{dir}\Users").GetDirectories())
            {
                if (skip.Contains(u.Name, StringComparer.OrdinalIgnoreCase) || !File.Exists(Path.Combine(u.FullName, "NTUSER.DAT"))) continue;
                var dst = Path.Combine(tmp, "u", u.Name);
                Directory.CreateDirectory(dst);
                foreach (var f in u.GetFiles("ntuser*")) f.CopyTo(Path.Combine(dst, f.Name), true);
                users.Add(u.Name);
            }
            return new Copied { Tmp = tmp, Users = users };
        }, quiet: true);
        try
        {
            var hive = ctx.Hives.Load("DSys", Path.Combine(tmp, "sys", "SYSTEM"));
            r.Layout = DisplayReg.ActiveLayout(hive);
        }
        catch { }
        finally { ctx.Hives.UnloadAll(); }
        return r;
    });

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var cands = ctx.ChildDisks();
        var src = v.Get("Source", cands.FirstOrDefault()?.Name ?? "").StripVhdx();
        var srcF = ctx.Cfg.VhdPath(src);
        var cp = Copy(ctx, srcF);
        var us = cp?.Users ?? [];
        return
        [
            new Field("Source", "Save from child disk", FieldKind.Choice, cands.FirstOrDefault()?.Name ?? "")
            {
                Choices = cands.Select(c => c.Name).ToArray(), Help = "The one you set up on the physical machine and shut down; base disks and mounted disks are filtered out",
            },
            new Field("Found", "In the child disk", FieldKind.Note, cp is null ? "Unreadable (missing or mounted)" : $"{us.Count} users: {string.Join(", ", us)}; most recent layout with all displays connected: {cp.Layout.Count} displays"),
            new Field("User", "Scaling per display", FieldKind.Choice, us.Contains(ctx.Cfg.User) ? ctx.Cfg.User : us.FirstOrDefault() ?? ctx.Cfg.User) { Choices = us.ToArray(), Help = "HKCU PerMonitorSettings" },
            new Field("Name", "Profile name", FieldKind.Text, $"{src}-{cp?.Layout.Count ?? 0}screens-{DateTime.Now:yyyyMMdd}") { Help = $"Saved to {ctx.Cfg.DisplayDir}\\<profile name>\\" },
        ];
    }

    public override CheckList Check(Context ctx, Values v)
    {
        var c = new CheckList();
        var src = v.Get("Source").StripVhdx();
        var srcF = ctx.Cfg.VhdPath(src);
        var cp = Copy(ctx, srcF);
        var us = cp?.Users ?? [];
        var user = v.Get("User");
        var name = v.Get("Name");
        var outDir = Path.Combine(ctx.Cfg.DisplayDir, name);
        c.Admin();
        c.Add("Child disk exists", src != "" && File.Exists(srcF), srcF);
        c.Add("Not a base disk", !src.StartsWith(Config.BasePrefix, StringComparison.OrdinalIgnoreCase), "A base disk has never booted on a physical machine, so it has no display settings");
        c.Add("Child disk not mounted", !Storage.IsAttached(srcF), "A running or mounted disk does not give the latest settings");
        ctx.CheckMountRoot(c);
        c.Add("Registry read", cp is not null, cp is not null ? $"SYSTEM and NTUSER of {us.Count} users" : "Read-only mount or copy failed");
        c.Add("Has display profile", cp is { Layout.Count: > 0 }, cp is { Layout.Count: > 0 } ? $"Most recent layout with all displays connected: {cp.Layout.Count} displays" : "No Configuration (this disk has not booted on a physical machine with displays connected)");
        c.Add("User exists", us.Contains(user, StringComparer.OrdinalIgnoreCase), $"{user} (per-display scaling PerMonitorSettings)");
        c.FileName("Profile name", name);
        c.Add("Profile name available", !Directory.Exists(outDir) && !File.Exists(outDir), Directory.Exists(outDir) ? $"{outDir} already exists, use another profile name" : outDir);
        if (cp is { Layout.Count: > 0 }) c.Extra = new(DisplayReg.LayoutHeaders, [.. cp.Layout]);
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var src = v.Get("Source").StripVhdx();
        var srcF = ctx.Cfg.VhdPath(src);
        var cp = Copy(ctx, srcF) ?? throw new WlbError("Cannot read the registry of the child disk");
        var user = v.Get("User");
        var name = v.Get("Name");
        var outDir = Path.Combine(ctx.Cfg.DisplayDir, name);
        var tmp = cp.Tmp;
        try
        {
            ctx.Stage("Export registry", $@"{src} → {outDir}\display.reg");
            var sys = ctx.Hives.Load("DSys", Path.Combine(tmp, "sys", "SYSTEM"));
            var usr = ctx.Hives.Load("DUsr", Path.Combine(tmp, "u", user, "NTUSER.DAT"));
            List<string> regParts = [];
            var srcKeys = new[] { "Configuration", "Connectivity", "MonitorDataStore", "ScaleFactors" }
                .Select(x => $@"HKLM\{sys}\ControlSet001\Control\GraphicsDrivers\{x}")
                .Append($@"HKLM\{usr}\Control Panel\Desktop\PerMonitorSettings");
            foreach (var k in srcKeys)
            {
                if (!Hives.KeyExists(k.Substring(5))) { ctx.Say($"    Skipped (not found): {k}", Level.Warn); continue; }
                // Path prefixes are replaced with the standard paths of the online system, and replaced with the child disk's hive again on restore
                var txt = RegFile.Export(k.Substring(5));
                txt = Regex.Replace(txt, Regex.Escape($@"HKEY_LOCAL_MACHINE\{sys}\ControlSet001"), @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet", RegexOptions.IgnoreCase);
                txt = Regex.Replace(txt, Regex.Escape($@"HKEY_LOCAL_MACHINE\{usr}"), "HKEY_CURRENT_USER", RegexOptions.IgnoreCase);
                regParts.Add(txt.TrimEnd());
                ctx.Say($"    {k}");
            }
            ctx.Hives.UnloadAll();
            Directory.CreateDirectory(outDir);
            var regFile = Path.Combine(outDir, "display.reg");
            Files.WriteText(regFile, RegFile.Header + "\r\n\r\n" + string.Join("\r\n\r\n", regParts) + "\r\n", Files.Utf16);

            ctx.Stage("Write README.md");
            var layout = cp.Layout;
            var tbl = string.Join("\r\n", layout.Select(r => $"| {string.Join(" | ", r)} |"));
            Files.WriteText(Path.Combine(outDir, "README.md"),
                $"""
                    # Display settings: {name}

                    - Source: `{srcF}` (HKLM part) and the per-display scaling of user `{user}` in it (HKCU part)
                    - Saved at: {DateTime.Now:yyyy-MM-dd HH:mm}, saved with `wlb display`
                    - Restore: tick "Restore display settings" when creating a child disk, profile name {name} (`wlb layer --Display Y --DisplayName {name}`)

                    Layout most recently used with all displays connected ({layout.Count} displays, taken from Recent in Connectivity):

                    | Display | Resolution | Position | Refresh rate | Rotation | HDR | Primary display |
                    |---|---|---|---|---|---|---|
                    {tbl}

                    Only valid for the same machine with the same displays connected to the same ports.
                    """.Replace("\r\n", "\n").Replace("\n", "\r\n"), Files.Utf8Bom);

            ctx.Title("Save complete");
            var keyCount = File.ReadAllLines(regFile, Encoding.Unicode).Count(l => l.StartsWith('['));
            ctx.Result("Profile", outDir);
            ctx.Result("File", $"display.reg ({Math.Round(new FileInfo(regFile).Length / 1024.0)} KB, {keyCount} keys), README.md");
            ctx.Result("Log", ctx.LogPath);
        }
        finally
        {
            ctx.Hives.UnloadAll();
            Files.TryDelete(tmp);
            _cache.Forget(srcF);
        }
    }
}
