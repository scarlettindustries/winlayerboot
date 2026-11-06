using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinLayerBoot.Core;

namespace WinLayerBoot.Ops;

/// <summary>Common part of boot entry management: only boot entries starting with WLB- are recognized.</summary>
public abstract class EntryOp : Operation
{
    public override string Category => "manage";
    public override string Lock => "entries";
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["Boot entry"] = "Entry", ["Current state"] = "Entry", ["VHDX exists"] = "Entry", ["New name"] = "Desc", ["New name not in use"] = "Desc",
    };

    /// <summary>targets = true: the selectable range is widened to non-VHD system boot entries (used for set default / next boot).</summary>
    protected static Field EntryField(Context ctx, bool targets = false)
    {
        var list = targets ? ctx.Bcd.BootTargets() : ctx.Bcd.WlbEntries();
        return new Field("Entry", "Boot entry", FieldKind.Choice, list.FirstOrDefault(e => e.IsWlb)?.Desc ?? list.FirstOrDefault()?.Desc ?? "")
        {
            Choices = list.Select(e => e.Desc).ToArray(),
            Help = targets ? $"Boot entries starting with {Config.EntryPrefix} and non-VHD system boot entries (such as Windows Server); a GUID also works"
                : list.Count > 0 ? $"Only boot entries starting with {Config.EntryPrefix} are listed; a GUID also works" : $"No boot entries starting with {Config.EntryPrefix} in BCD",
        };
    }

    protected static Bcd.Entry? Pick(Context ctx, Values v, bool targets = false) => targets ? ctx.Bcd.FindTarget(v.Get("Entry")) : ctx.Bcd.Find(v.Get("Entry"));

    protected static CheckList Basic(Context ctx, Values v, out Bcd.Entry? e, bool targets = false)
    {
        var c = new CheckList();
        e = Pick(ctx, v, targets);
        c.Admin();
        var scope = targets ? $"starting with {Config.EntryPrefix} or non-VHD" : $"starting with {Config.EntryPrefix}";
        c.Add("Boot entry", e is not null, e is not null ? $"{e.Id}  {e.Desc}  → {e.Target}{(e.Tier == "system" ? " (system boot entry; can only be set as default / next boot)" : "")}" : $"No boot entry named \"{v.Get("Entry")}\" found ({scope})");
        return c;
    }
}

/// <summary>Operations that pick one boot entry and change one thing in the BCD (delete, hide, show, set default, next boot): the only field is "boot entry"; checks the current state and runs one command.</summary>
public abstract class SimpleEntryOp : EntryOp
{
    /// <summary>true: non-VHD system boot entries can also be selected.</summary>
    protected virtual bool Targets => false;
    protected virtual void CheckState(CheckList c, Bcd.Entry e) { }
    /// <summary>Changes the BCD and returns a description of the result.</summary>
    protected abstract string Apply(Context ctx, Bcd.Entry e);

    public override IReadOnlyList<Field> Fields(Context ctx, Values v) => [EntryField(ctx, Targets)];

    public override CheckList Check(Context ctx, Values v)
    {
        var c = Basic(ctx, v, out var e, Targets);
        if (e is not null) CheckState(c, e);
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var e = Pick(ctx, v, Targets) ?? throw new WlbError("Boot entry not found");
        ctx.Say("  " + Apply(ctx, e), Level.Ok);
    }
}

/// <summary>Adds a boot entry from a VHD: mounts the disk, creates it anew with bcdboot, repoints it to the VHD; the description starts with WLB-.</summary>
public sealed class EntryAddOp : EntryOp
{
    public override string Name => "entry-add";
    public override string Title => "Add boot entry from VHD";
    public override string Summary => "Add a boot entry for disks in the vhd folder that have no WLB boot entry yet (bcdboot creates it and BCD is verified)";
    public override string[] Stages => ["Create boot entry", "Next boot and default entry"];
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["VHDX exists"] = "Vhd", ["Not mounted"] = "Vhd", ["Not a base disk"] = "Vhd", ["No WLB boot entry yet"] = "Vhd", ["Boot entry name"] = "Desc", ["Name not in use"] = "Desc",
    };

    private static List<Context.Disk> Candidates(Context ctx)
    {
        var have = ctx.Bcd.WlbEntries().Select(e => e.Vhd).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ctx.Disks().Where(d => !d.IsBase && !d.Attached && !have.Contains(d.Path)).ToList();
    }

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var cands = Candidates(ctx);
        var vhd = v.Get("Vhd", cands.FirstOrDefault()?.Name ?? "").StripVhdx();
        return
        [
            new Field("Vhd", "VHDX", FieldKind.Choice, cands.FirstOrDefault()?.Name ?? "")
            {
                Choices = cands.Select(c => c.Name).ToArray(), Help = cands.Count > 0 ? "Child disks in the vhd folder that have no WLB boot entry yet" : "All child disks in the vhd folder already have a WLB boot entry",
            },
            new Field("Desc", "Boot menu name", FieldKind.Text, Config.EntryPrefix + vhd) { Help = $"Must start with {Config.EntryPrefix}" },
            new Field("Ems", "Enable serial EMS", FieldKind.Bool, "Y") { Help = "Turn on if the child disk has SAC set up" },
            .. Common.BootFields("it"),
        ];
    }

    public override CheckList Check(Context ctx, Values v)
    {
        var c = new CheckList();
        var vhd = ctx.Cfg.VhdPath(v.Get("Vhd"));
        var desc = v.Get("Desc");
        var have = ctx.Bcd.ForVhd(vhd);
        c.Admin();
        c.WorkHost(ctx);
        c.Add("VHDX exists", File.Exists(vhd), vhd);
        c.Add("Not mounted", !Storage.IsAttached(vhd), "A mounted disk cannot get a boot entry");
        c.Add("Not a base disk", !Path.GetFileName(vhd).StartsWith(Config.BasePrefix, StringComparison.OrdinalIgnoreCase), "A base disk is read-only and cannot boot");
        c.Add("No WLB boot entry yet", have is null, have is not null ? $"Already has {have.Id}  {have.Desc}" : "Can be created");
        c.EntryDesc(desc);
        c.Add("Name not in use", ctx.Bcd.WlbEntries().All(x => !x.Desc.EqualsIgnoreCase(desc)), desc);
        ctx.CheckMountRoot(c);
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var vhd = ctx.Cfg.VhdPath(v.Get("Vhd"));
        var desc = v.Get("Desc");
        ctx.Stage("Create boot entry", $"Mount {vhd}, create with bcdboot");
        var id = ctx.Bcd.AddEntryForVhd(vhd, desc, ems: v.Bool("Ems", true));
        ctx.Stage("Next boot and default entry");
        Common.ApplyBoot(ctx, id, v, "it");
        ctx.Result("Boot entry", $"{id}  {desc}  → {vhd}");
    }
}

public sealed class EntryRenameOp : EntryOp
{
    public override string Name => "entry-rename";
    public override string Title => "Rename boot entry";
    public override string Summary => "Change the name shown in the boot menu (must still start with WLB-)";

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var e = Pick(ctx, v);
        return [EntryField(ctx), new Field("Desc", "New name", FieldKind.Text, e?.Desc ?? Config.EntryPrefix)];
    }

    public override CheckList Check(Context ctx, Values v)
    {
        var c = Basic(ctx, v, out var e);
        var desc = v.Get("Desc");
        c.Add("New name", Bcd.IsWlbDesc(desc) && desc.Length > Config.EntryPrefix.Length, $"{desc} (must start with {Config.EntryPrefix})");
        c.Add("New name not in use", ctx.Bcd.WlbEntries().All(x => x.Id.EqualsIgnoreCase(e?.Id) || !x.Desc.EqualsIgnoreCase(desc)), desc);
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var e = Pick(ctx, v) ?? throw new WlbError("Boot entry not found");
        ctx.Bcd.Rename(e.Id, v.Get("Desc"));
        ctx.Say($"  {e.Id}: {e.Desc} → {v.Get("Desc")}", Level.Ok);
    }
}

public sealed class EntryDeleteOp : SimpleEntryOp
{
    public override string Name => "entry-delete";
    public override string Title => "Delete boot entry";
    public override string Summary => "Delete the boot entry from BCD; the VHDX file is left alone";

    protected override void CheckState(CheckList c, Bcd.Entry e) =>
        c.Add("VHDX file", true, e.VhdExists ? $"{e.Vhd} is kept; you can add it back later with 'Add from VHD'" : "File no longer exists");

    protected override string Apply(Context ctx, Bcd.Entry e) { ctx.Bcd.Delete(e.Id); return $"Deleted {e.Id}  {e.Desc}"; }
}

public sealed class EntryHideOp : SimpleEntryOp
{
    public override string Name => "entry-hide";
    public override string Title => "Hide boot entry";
    public override string Summary => "Remove from the boot menu (the entry stays and can be shown again)";

    protected override void CheckState(CheckList c, Bcd.Entry e) => c.Add("Current state", !e.Hidden, e.Hidden ? "Already hidden" : "In the menu", warn: e.Hidden);

    protected override string Apply(Context ctx, Bcd.Entry e) { ctx.Bcd.Hide(e.Id); return $"Hidden {e.Id}  {e.Desc}"; }
}

public sealed class EntryShowOp : SimpleEntryOp
{
    public override string Name => "entry-show";
    public override string Title => "Show boot entry";
    public override string Summary => "Add a hidden boot entry back to the end of the boot menu";

    protected override void CheckState(CheckList c, Bcd.Entry e) => c.Add("Current state", e.Hidden, e.Hidden ? "Hidden" : "Already in the menu", warn: !e.Hidden);

    protected override string Apply(Context ctx, Bcd.Entry e)
    {
        if (!e.Hidden) return "Already in the menu";
        ctx.Bcd.Show(e.Id);
        return $"Shown {e.Id}  {e.Desc}";
    }
}

public sealed class EntryDefaultOp : SimpleEntryOp
{
    public override string Name => "entry-default";
    public override string Title => "Set as default boot entry";
    public override string Summary => "bcdedit /default: boots this entry when the boot menu times out, permanently ('Next boot' applies only once); non-VHD system boot entries can also be chosen";
    protected override bool Targets => true;

    protected override void CheckState(CheckList c, Bcd.Entry e)
    {
        if (e.IsVhd) c.Add("VHDX exists", e.VhdExists, e.Vhd);
        c.Add("Current state", !e.IsDefault, e.IsDefault ? "Already the default boot entry" : e.Hidden ? "Hidden (the menu timeout will still boot it once it is the default)" : "In the menu", warn: e.IsDefault);
    }

    protected override string Apply(Context ctx, Bcd.Entry e) { ctx.Bcd.SetDefault(e.Id); return $"Default boot entry → {e.Id}  {e.Desc}"; }
}

public sealed class EntryNextOp : SimpleEntryOp
{
    public override string Name => "entry-next";
    public override string Title => "Set as next boot";
    public override string Summary => "bootsequence: boots this entry on the next startup without showing the menu, once only; non-VHD system boot entries can also be chosen";
    protected override bool Targets => true;

    protected override void CheckState(CheckList c, Bcd.Entry e) { if (e.IsVhd) c.Add("VHDX exists", e.VhdExists, e.Vhd); }

    protected override string Apply(Context ctx, Bcd.Entry e) { ctx.Bcd.BootNext(e.Id); return $"Next startup boots {e.Id}  {e.Desc} (once only)"; }
}
