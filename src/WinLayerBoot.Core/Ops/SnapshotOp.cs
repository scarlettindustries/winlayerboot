using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;

namespace WinLayerBoot.Ops;

/// <summary>Takes a snapshot of a child disk: sets the parent disk read-only, creates a new differencing disk with it as parent, copies the boot entry.</summary>
public sealed class SnapshotOp : Operation
{
    public override string Name => "snapshot";
    public override string Title => "Take snapshot";
    public override string Summary => "Set the parent disk read-only, create a differencing disk with it as parent (instant), copy or create a boot entry";
    public override string[] Steps =>
    [
        "Set parent disk read-only, create differencing disk",
        "Copy the WLB boot entry of the parent disk and point it at the new disk (if the parent has none, create one with bcdboot and verify the BCD)",
        "Hide the parent boot entry and set next boot as chosen. No reboot",
    ];

    public override string[] Stages => ["Set read-only and create differencing disk", "Add boot entry", "Parent boot entry and next boot"];
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["Parent disk exists"] = "Parent", ["Parent disk is not a base disk"] = "Parent", ["Parent disk not mounted"] = "Parent", ["Parent disk read-only"] = "Parent", ["Parent boot entry"] = "Parent",
        ["New disk name"] = "Child", ["New disk file name available"] = "Child", ["Boot entry name"] = "Desc",
    };

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var cands = ctx.ChildDisks();
        var parent = v.Get("Parent", cands.FirstOrDefault()?.Name ?? "").StripVhdx();
        var i = 1;
        while (File.Exists(ctx.Cfg.VhdPath($"{parent}-snap{i}"))) i++;
        var newNm = v.Get("Child", $"{parent}-snap{i}").StripVhdx();
        var pEntry = ctx.Bcd.ForVhd(ctx.Cfg.VhdPath(parent));
        var sm = pEntry is null ? Match.Empty : Regex.Match(pEntry.Desc, @"(\(.*\))\s*$");
        var suffix = sm.Success ? " " + sm.Groups[1].Value : "";
        return
        [
            new Field("Parent", "Disk to snapshot", FieldKind.Choice, cands.FirstOrDefault()?.Name ?? "")
            {
                Choices = cands.Select(c => c.Name).ToArray(),
                Help = cands.Count > 0 ? "Base disks and mounted disks are filtered out; most recently modified first" : $"No child disks to snapshot in {ctx.Cfg.VhdDir}",
            },
            new Field("ParentEntry", "Parent boot entry", FieldKind.Note, pEntry is not null ? $"{pEntry.Id}  {pEntry.Desc} (copied for the new disk)" : "No WLB boot entry; bcdboot will create one for the new disk"),
            new Field("Child", "New disk name", FieldKind.Text, $"{parent}-snap{i}"),
            new Field("Desc", "Boot menu name", FieldKind.Text, $"{Config.EntryPrefix}{newNm}{suffix}") { Help = $"Must start with {Config.EntryPrefix}; by default reuses the part in parentheses from the parent boot entry" },
            new Field("HideParent", "Hide parent entry", FieldKind.Bool, "Y") { Help = "Booting from the parent disk once it is read-only corrupts the new disk; the entry stays and can be shown again" },
            .. Common.BootFields("New disk", nextOn: true),
        ];
    }

    public override CheckList Check(Context ctx, Values v)
    {
        var c = new CheckList();
        var parent = v.Get("Parent").StripVhdx();
        var parentF = ctx.Cfg.VhdPath(parent);
        var newNm = v.Get("Child").StripVhdx();
        var pEntry = ctx.Bcd.ForVhd(parentF);
        var isBase = parent.StartsWith(Config.BasePrefix, StringComparison.OrdinalIgnoreCase);
        c.Admin();
        c.WorkHost(ctx);
        c.Add("Parent disk exists", parent != "" && File.Exists(parentF), parentF);
        c.Add("Parent disk is not a base disk", !isBase, isBase ? $"{Config.BasePrefix}* is a base disk; only child disks can be snapshotted" : parent);
        c.Add("Parent disk not mounted", !Storage.IsAttached(parentF), "A mounted disk cannot be snapshotted");
        if (File.Exists(parentF))
        {
            var ro = new FileInfo(parentF).IsReadOnly;
            c.Add("Parent disk read-only", ro, ro ? "Already read-only" : "Will be set read-only on run", warn: true);
        }
        c.FileName("New disk name", newNm, noBasePrefix: true);
        c.NewFile("New disk file name available", ctx.Cfg.VhdPath(newNm));
        c.EntryDesc(v.Get("Desc"));
        if (pEntry is not null) c.Add("Parent boot entry", true, $"{pEntry.Id}  {pEntry.Desc} (copied for the new disk)");
        else
        {
            c.Add("Parent boot entry", false, "The parent disk has no WLB boot entry; bcdboot will create one for the new disk", warn: true);
            ctx.CheckMountRoot(c);
        }
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var parentF = ctx.Cfg.VhdPath(v.Get("Parent"));
        var newF = ctx.Cfg.VhdPath(v.Get("Child"));
        var desc = v.Get("Desc");
        var pEntry = ctx.Bcd.ForVhd(parentF);
        try
        {
            ctx.Stage("Set read-only and create differencing disk", $"Set {parentF} read-only, create {newF}");
            File.SetAttributes(parentF, File.GetAttributes(parentF) | FileAttributes.ReadOnly);
            Storage.CreateDifferencing(newF, parentF);

            ctx.Stage("Add boot entry");
            var id = pEntry is not null ? ctx.Bcd.CopyEntry(pEntry.Id, newF, desc) : ctx.Bcd.AddEntryForVhd(newF, desc, ems: false);

            ctx.Stage("Parent boot entry and next boot");
            if (v.Bool("HideParent", true) && pEntry is not null)
            {
                ctx.Bcd.Hide(pEntry.Id);
                ctx.Say($"    Parent boot entry {pEntry.Id} hidden from the menu (the entry still exists; \"Boot entries → Show\" restores it)");
            }
            else if (pEntry is not null) ctx.Say("    The parent boot entry stays in the menu. Note: booting from the parent disk writes to it and corrupts the new disk.", Level.Warn);
            Common.ApplyBoot(ctx, id, v, "New disk", nextOn: true);

            ctx.Title("Snapshot complete");
            ctx.Result("Parent disk", $"{parentF} (read-only)");
            ctx.Result("New disk", $"{newF}({Files.MB(new FileInfo(newF).Length)} MB)");
            ctx.Result("Boot entry", $"{id}  {desc}");
            ctx.Result("Log", ctx.LogPath);
            ctx.Say("  This program does not reboot.", Level.Warn);
        }
        catch
        {
            ctx.Say($"The new disk is kept at {newF} and the parent disk is set read-only. After confirming the cause, delete the new disk and its boot entry, then rerun.", Level.Warn);
            throw;
        }
    }
}
