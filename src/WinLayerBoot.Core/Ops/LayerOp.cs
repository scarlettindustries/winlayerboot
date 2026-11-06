using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using WinLayerBoot.Core;
using WinLayerBoot.Customize;

namespace WinLayerBoot.Ops;

/// <summary>Creates a child disk: creates the differencing disk, writes the unattend file, applies customizations offline one by one, adds a boot entry.</summary>
public sealed class LayerOp : Operation
{
    public override string Name => "layer";
    public override string Title => "Create child disk";
    public override string Summary => "Create a differencing child disk on the base disk, write the unattend file and customizations offline, add a boot entry";
    public override string[] Steps =>
    [
        "Create the differencing child disk and mount it (no drive letter assigned)",
        "Write unattend.xml: account, base disk language + optional English input method, skip OOBE; add remote desktop and turn off the firewall as selected",
        @"Write the preset of this run to C:\wlb-layer-<git hash>.json on the child disk as selected (no password); it can be imported to redo",
        "Offline customization: run each selected customization in turn",
        "bcdboot adds the boot entry (WLB- prefix); any other BCD settings it changed are all restored",
        "Unmount the child disk; optionally set as next boot (bootsequence, once only). No reboot",
    ];

    public override string[] Stages => ["Create differencing child disk and mount", "Write unattend file", "Offline customization", "Add boot entry", "Unmount child disk", "Next boot and default entry"];
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["Base disk exists"] = "Base", ["Base disk read-only"] = "Base", ["Base disk not mounted"] = "Base", ["Base disk language"] = "Base", ["Base disk hash matches"] = "VerifyHash",
        ["Child disk name"] = "Child", ["New child disk file name available"] = "Child", ["Boot entry name"] = "Desc", ["Username"] = "User", ["Password"] = "Pass",
        ["virtio ISO"] = "Virtio", ["Machine name"] = "Machine", ["Physical machine driver folder"] = "Machine", ["Display profile"] = "DisplayName",
        ["LOF folder"] = "Sac", ["Public key format"] = "PubKey",
    };

    private LayerPlan Plan(Context ctx, Values v, out string baseVhdx)
    {
        var bases = ctx.BaseDisks();
        var name = v.Get("Base", bases.FirstOrDefault()?.Name ?? "").StripVhdx();
        baseVhdx = ctx.Cfg.VhdPath(name);
        return new LayerPlan(ctx, baseVhdx, ctx.BaseIntl(baseVhdx));
    }

    private static string Stem(string baseName) => Regex.Replace(baseName, "^" + Regex.Escape(Config.BasePrefix), "", RegexOptions.IgnoreCase);

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var bases = ctx.BaseDisks();
        var p = Plan(ctx, v, out var baseVhdx);
        var baseName = Path.GetFileNameWithoutExtension(baseVhdx);
        var intl = p.Intl;
        var isServer = p.IsServer;
        var user = isServer ? "Administrator" : v.Get("User", ctx.Cfg.User);
        // When the user name is not yet filled in settings, the child disk's default name uses only the base disk name, with no stray -
        var who = isServer ? "admin" : user;
        var childDefault = who == "" ? Stem(baseName) : $"{Stem(baseName)}-{who}";
        var child = v.Get("Child", childDefault);
        List<Field> f =
        [
            new Field("Base", "Base disk", FieldKind.Choice, bases.FirstOrDefault()?.Name ?? "")
            {
                Group = "Base disk and account", Choices = bases.Select(b => b.Name).ToArray(),
                Help = bases.Count > 0 ? $"Read-only {Config.BasePrefix}* disks in the vhd folder, newest first" : $"No base disk in {ctx.Cfg.VhdDir}; create a base disk first",
            },
            new Field("BaseInfo", "Base disk info", FieldKind.Note, intl is null ? "Cannot read (base disk missing or mounted)"
                : $"{intl.EditionID} ({(isServer ? "Server" : "Client")}), UI language {intl.UI}, region {intl.SysLocale}, time zone {intl.TimeZone}, default input method {intl.Keyboard}") { Group = "Base disk and account" },
        ];
        // Order: base disk - child disk name - boot menu name - SSH - SAC - user name - password | drivers | display | boot entry | other (remaining customization items and validation)
        f.Add(new Field("Child", "Child disk name", FieldKind.Text, childDefault) { Group = "Base disk and account", Help = $"vhd\\<name>.vhdx, same folder as the base disk; cannot start with {Config.BasePrefix}" });
        f.Add(new Field("Desc", "Boot menu name", FieldKind.Text, Context.DefaultDesc(child, intl)) { Group = "Base disk and account", Help = $"Must start with {Config.EntryPrefix}" });
        void AddCustom(Customization c, string group)
        {
            if (!c.Applies(p)) { f.Add(new Field(c.Key + "Note", c.Label, FieldKind.Note, "Not applicable: " + c.NotApplicable(p)) { Group = group }); return; }
            f.Add(new Field(c.Key, c.Label, FieldKind.Bool, c.DefaultOn ? "Y" : "N") { Group = group, Help = c.Help });
            if (c.On(v)) f.AddRange(c.ExtraFields(p, v).Select(x => x with { Group = group }));
        }
        Customization C(string key) => Customization.All.First(c => c.Key == key);
        AddCustom(C("Ssh"), "Base disk and account");
        AddCustom(C("Sac"), "Base disk and account");
        if (isServer) f.Add(new Field("UserNote", "Account", FieldKind.Note, "Server does not create a user; fixed to the built-in Administrator") { Group = "Base disk and account" });
        else f.Add(new Field("User", "Username", FieldKind.Text, ctx.Cfg.User) { Group = "Base disk and account", Help = "Local administrator; default comes from Settings" });
        f.Add(new Field("Pass", "Password", FieldKind.Password, ctx.Cfg.Pass) { Group = "Base disk and account", Help = "Written to the unattend file in plain text; default comes from Settings" });
        AddCustom(C("Virtio"), "Drivers");
        AddCustom(C("Drivers"), "Drivers");
        AddCustom(C("Display"), "Display");
        f.Add(new Field("AddBoot", "Add boot entry", FieldKind.Bool, "Y") { Group = "Boot entry", Help = "bcdboot adds the boot entry and verifies BCD; N leaves BCD completely untouched" });
        if (v.Bool("AddBoot", true)) f.AddRange(Common.BootFields("this child disk", group: "Boot entry"));
        string[] placed = ["Ssh", "Sac", "Virtio", "Drivers", "Display"];
        foreach (var c in Customization.All.Where(c => !placed.Contains(c.Key))) AddCustom(c, "Other");
        f.Add(new Field("VerifyHash", "Verify base SHA256", FieldKind.Bool, "N") { Group = "Other", Help = "About 1 minute; needs a .sha256 file" });
        f.Add(new Field("SavePreset", "Save preset on disk", FieldKind.Bool, "Y") { Group = "Other", Help = @"Writes C:\wlb-layer-<git hash>.json (no password); later you can use 'Import from VHD'" });
        return f;
    }

    private static List<Customization> Chosen(LayerPlan p, Values v) => Customization.All.Where(c => c.Applies(p) && c.On(v)).ToList();

    public override CheckList Check(Context ctx, Values v)
    {
        var c = new CheckList();
        var p = Plan(ctx, v, out var vhdx);
        var intl = p.Intl;
        var childNm = v.Get("Child").StripVhdx();
        var child = ctx.Cfg.VhdPath(childNm);
        var desc = v.Get("Desc");
        c.Admin();
        c.Add("Base disk exists", File.Exists(vhdx), vhdx);
        if (File.Exists(vhdx))
        {
            c.Add("Base disk read-only", new FileInfo(vhdx).IsReadOnly, "The parent disk was written to; all child disks would be corrupted");
            c.Add("Base disk not mounted", !Storage.IsAttached(vhdx), "A mounted base disk cannot be a parent disk");
            if (!v.Bool("VerifyHash")) c.Add("Base disk hash matches", true, "Not verified", skip: true);
            else if (ctx.Quick && File.Exists(vhdx + ".sha256")) c.Add("Base disk hash matches", true, "Verified at start (about 1 minute)", skip: true);
            else if (File.Exists(vhdx + ".sha256"))
            {
                ctx.Say("  Verifying base disk hash (about 1 minute)...");
                var ok = Files.Sha256(vhdx).EqualsIgnoreCase(File.ReadAllText(vhdx + ".sha256").Trim());
                c.Add("Base disk hash matches", ok, vhdx + ".sha256");
            }
            else c.Add("Base disk hash matches", false, "No .sha256 file (not written when the base disk was created), cannot verify", warn: true);
        }
        c.Add("Base disk language", intl is not null && intl.UI != "" && intl.Keyboard != "",
            intl is not null ? $"{intl.EditionID}, {intl.UI}, input method {Unattend.InputLocale(p, v)}" : "Cannot read (base disk missing or mounted)");
        c.FileName("Child disk name", childNm, noBasePrefix: true);
        c.NewFile("New child disk file name available", child, "Pick another name, or delete it and its boot entry first");
        if (v.Bool("AddBoot", true))
        {
            c.EntryDesc(desc);
            c.WorkHost(ctx);
        }
        else c.Add("Run on the work host", true, "No boot entry added, BCD unchanged", skip: true);
        const string empty = "Not set. Fill it in here, or set a default in 'Settings'";
        if (!p.IsServer) c.Add("Username", Regex.IsMatch(v.Get("User"), @"^[A-Za-z0-9_.-]{1,20}$"), v.Get("User") is { Length: > 0 } un ? un : empty);
        c.Add("Password", v.Has("Pass"), v.Has("Pass") ? "Set" : empty);
        ctx.CheckMountRoot(c);
        foreach (var cu in Chosen(p, v)) cu.Check(p, v, c);
        c.Dir("VHDX folder", ctx.Cfg.VhdDir);
        c.FreeSpace(ctx.Cfg.VhdDir, 70, "A child disk is up to 64 GB", warn: true);
        return c;
    }

    /// <summary>Imports the preset of another child disk. The child disk name and boot menu name are not taken: the originals are already in use, so defaults are recomputed from the new preset.</summary>
    public Values FromVhd(Context ctx, string vhdx)
    {
        var v = FromPreset(Preset.FromVhd(ctx, vhdx, Name));
        v.Remove("Child");
        v.Remove("Desc");
        return v;
    }

    public override void Run(Context ctx, Values v)
    {
        var p = Plan(ctx, v, out var vhdx);
        var intl = p.Intl ?? throw new WlbError("Cannot read base disk info");
        var isServer = p.IsServer;
        var user = isServer ? "Administrator" : v.Get("User");
        var pass = v.Get("Pass");
        var childNm = v.Get("Child").StripVhdx();
        var child = ctx.Cfg.VhdPath(childNm);
        var desc = v.Get("Desc");
        var addBoot = v.Bool("AddBoot", true);
        var chosen = Chosen(p, v);
        var sac = chosen.Any(c => c is Sac);
        var preset = ToPreset(v, Fields(ctx, v), "wlb");
        string? id = null;
        try
        {
            Directory.CreateDirectory(ctx.Cfg.VhdDir);
            ctx.Stage("Create differencing child disk and mount", child);
            Storage.CreateDifferencing(child, vhdx);
            id = ctx.Mnt.WithWindows(child, readOnly: false, "win", (w, parts) =>
            {
                var label = Storage.VolumeOf(ctx.Mnt.WindowsPart(parts, child))?.Label ?? "";
                if (!label.EqualsIgnoreCase("Win11")) throw new WlbError($"The largest partition of the child disk has volume label \"{label}\", not Win11");
                ctx.Say($"    Windows partition → {w}");
                string? newId = null;
                try
                {
                    ctx.Stage("Write unattend file", "unattend.xml");
                    Directory.CreateDirectory($@"{w}\Windows\Panther");
                    var xml = Unattend.Build(p, v, user, pass, ctx.Bcd.MenuLocale(w));
                    var unattendFile = $@"{w}\Windows\Panther\unattend.xml";
                    Files.WriteText(unattendFile, xml, Files.Utf8Bom);
                    new XmlDocument().Load(unattendFile);
                    if (v.Bool("SavePreset", true))
                    {
                        var presetFile = Preset.FileName(Name);
                        preset.Save($@"{w}\{presetFile}");
                        ctx.Say($@"    Preset written to C:\{presetFile} (no password)");
                    }
                    ctx.Say($"    Language {intl.UI}, input method {Unattend.InputLocale(p, v)}, time zone {(intl.TimeZone != "" ? intl.TimeZone : "UTC")}");
                    ctx.Say($"    XML is well-formed ({string.Join(", ", Customization.All.Where(c => !c.HasStep && c.Applies(p)).Select(c => $"{c.Label} {(c.On(v) ? "On" : "Unchanged")}"))})");

                    ctx.Stage("Offline customization");
                    var steps = Customization.All.Where(c => c.HasStep).ToList();
                    var run = new LayerRun(p, v, w);
                    for (var i = 0; i < steps.Count; i++)
                    {
                        var c = steps[i];
                        var no = $"3.{i + 1}";
                        if (!c.Applies(p)) { ctx.Say($"  {no} Skipped {c.Label} ({c.NotApplicable(p)})", Level.Dim); continue; }
                        if (!c.On(v)) { ctx.Say($"  {no} Skipped {c.Label}", Level.Dim); continue; }
                        ctx.Say($"  {no} {c.Label}");
                        c.Apply(run);
                        ctx.Hives.UnloadAll();
                    }

                    ctx.Stage("Add boot entry", addBoot ? "" : "Skipped, BCD unchanged. You can add it later with 'Boot entries -> Add from VHD'.", skipped: !addBoot);
                    if (addBoot) newId = ctx.Bcd.AddEntryWithBcdboot(w, child, desc, ems: sac);
                }
                finally { ctx.Hives.UnloadAll(); }

                ctx.Stage("Unmount child disk");
                ctx.Mnt.UnmountDir(w);
                ctx.Mnt.DetachVhd(child);
                ctx.Say($"    Unmounted {child}");
                return newId;
            });

            ctx.Stage("Next boot and default entry", addBoot ? "" : "Skipped", skipped: !addBoot);
            if (addBoot) Common.ApplyBoot(ctx, id!, v, "this child disk");

            ctx.Title("Child disk done");
            ctx.Result("Child disk", $"{child} ({Files.MB(new FileInfo(child).Length)} MB)");
            ctx.Result("Boot entry", addBoot ? $"{id}  {desc}" : "Not added");
            ctx.Result("Account", $"{user}{(isServer ? " (Server built-in Administrator)" : "")}");
            ctx.Result("Customizations", string.Join(", ", chosen.Select(c => c.SummaryName(v))));
            ctx.Result("Log", ctx.LogPath);
            ctx.Say("  This program does not reboot.", Level.Warn);
        }
        catch
        {
            ctx.Say($"The child disk is kept at {child}. Find the cause, then delete the child disk and its boot entry and rerun.", Level.Warn);
            throw;
        }
        finally { ctx.Hives.UnloadAll(); }
    }
}
