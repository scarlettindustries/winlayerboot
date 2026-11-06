using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;
using WinLayerBoot.Customize;

namespace WinLayerBoot.Ops;

/// <summary>Merges the differencing chain behind a boot entry and exports it as a VHDX that can boot on its own.</summary>
public sealed class ExportOp : Operation
{
    public override string Name => "export";
    public override string Title => "Export as standalone disk";
    public override string Summary => "Merge the whole differencing chain into one VHDX that does not depend on the base disk and can boot on its own (for VMs)";
    public override string[] Steps =>
    [
        "Merge the whole differencing chain into one standalone dynamic disk, keeping the partition layout as is (the base disk must have a reserved ESP)",
        "Optional extra space: delete the recovery partition after the Windows partition, enlarge the virtual disk, extend the Windows partition",
        "Inject virtio drivers if needed; bcdboot /s writes boot files to the reserved ESP; if the original boot entry has EMS on, turn it on in the exported disk BCD too",
        "The system BCD must not change: after bcdboot, compare item by item and restore any item that changed",
        "Unmount the exported disk and print the conversion commands to run on the hypervisor host",
    ];

    public override string[] Stages => ["Merge differencing chain", "Extra space", "Mount exported disk", "Drivers and boot files", "Unmount exported disk"];
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["Boot entry"] = "Entry", ["Child disk exists"] = "Entry", ["Differencing chain"] = "Entry", ["Base disk has reserved ESP"] = "Entry",
        ["Extra space"] = "ExpandGB", ["Name"] = "Name", ["Target file does not exist"] = "Name", ["vioscsi"] = "Virtio",
    };

    private sealed class Facts
    {
        public string Layout = "";
        public bool HasEsp, HasVioscsi, IsServer;
        public double UsedGB;
    }

    private static readonly ProbeCache<Facts> _facts = new(cacheNull: true);

    /// <summary>Mounts the child disk read-only for a quick look: partition layout (whether there is a reserved ESP), whether vioscsi is present, whether it is Server, used space. Cached.</summary>
    private static Facts? GetFacts(Context ctx, string vhdx) => _facts.Get(vhdx, () =>
    {
        ctx.Say("  Mounting child disk read-only, checking partitions and drivers...", Level.Dim);
        return ctx.Mnt.WithWindows(vhdx, readOnly: true, "src", (dir, parts) =>
        {
            var vol = Storage.VolumeOf(ctx.Mnt.WindowsPart(parts, vhdx));
            var espVol = parts.Count > 0 ? Storage.VolumeOf(parts[0]) : null;
            var repo = $@"{dir}\Windows\System32\DriverStore\FileRepository";
            return new Facts
            {
                Layout = string.Join(" → ", parts.Select(p => $"{p.Type} {Files.MB(p.Size)} MB")),
                HasEsp = parts.Count > 0 && parts[0].Type == "System" && espVol?.FileSystem == "FAT32",
                HasVioscsi = Directory.Exists(repo) && Directory.GetDirectories(repo, "vioscsi.inf_amd64_*").Length > 0,
                IsServer = File.Exists($@"{dir}\Windows\System32\ServerManager.exe"),
                UsedGB = vol is null ? 0 : Math.Round((vol.Size - vol.SizeRemaining) / (double)(1L << 30), 1),
            };
        }, quiet: true);
    });

    private static List<Bcd.Entry> Candidates(Context ctx) =>
        ctx.Bcd.WlbEntries().Where(e => e.VhdExists).OrderByDescending(e => File.GetLastWriteTime(e.Vhd)).ToList();

    private static Bcd.Entry? Pick(List<Bcd.Entry> list, string sel) =>
        list.FirstOrDefault(e => e.Desc.EqualsIgnoreCase(sel) || e.Id.EqualsIgnoreCase(sel) || Path.GetFileNameWithoutExtension(e.Vhd).EqualsIgnoreCase(sel.StripVhdx()));

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var list = Candidates(ctx);
        var entry = Pick(list, v.Get("Entry", list.FirstOrDefault()?.Desc ?? ""));
        var src = entry?.Vhd ?? "";
        var srcGB = src != "" ? (long)Math.Ceiling((Storage.DiskImage(src)?.Size ?? 0) / (double)(1L << 30)) : 0;
        return
        [
            new Field("Entry", "Boot entry to export", FieldKind.Choice, list.FirstOrDefault()?.Desc ?? "")
            {
                Choices = list.Select(e => e.Desc).ToArray(),
                Help = list.Count > 0 ? "WLB boot entries pointing to a VHDX, most recently used first; a boot entry GUID or VHDX name also works" : "No WLB boot entry in BCD points to a VHDX",
            },
            new Field("Source", "Source", FieldKind.Note, src != "" ? $"{src} ({Context.Chain(src).Count} layers of differencing chain, {srcGB} GB)" : "(none)"),
            new Field("Name", "Exported disk name", FieldKind.Text, src != "" ? Path.GetFileNameWithoutExtension(src) : "export") { Help = $"Output {ctx.Cfg.ExportDir}\\<name>.vhdx" },
            new Field("ExpandGB", "Extra space GB", FieldKind.Int, "0") { Help = $"Added on top of the current {srcGB} GB; when greater than 0, deletes the recovery partition after the Windows partition, enlarges the virtual disk and extends the Windows partition; 0 means no extension" },
            new Field("Virtio", "VM uses virtio disk", FieldKind.Bool, "Y") { Help = "Inject from the virtio ISO when the child disk has no vioscsi" },
        ];
    }

    public override CheckList Check(Context ctx, Values v)
    {
        var c = new CheckList();
        var list = Candidates(ctx);
        var entry = Pick(list, v.Get("Entry"));
        var src = entry?.Vhd ?? "";
        var name = v.Get("Name").StripVhdx();
        var outF = ctx.Cfg.ExportPath(name);
        var expand = v.Int("ExpandGB");
        var virtio = v.Bool("Virtio", true);
        var chain = src != "" ? Context.Chain(src) : [];
        var facts = src != "" ? GetFacts(ctx, src) : null;
        var needVio = virtio && facts is not null && !facts.HasVioscsi;
        var vioOs = facts?.IsServer == true ? "2k25" : "w11";
        c.Admin();
        c.WorkHost(ctx);
        c.Add("Boot entry", entry is not null, entry is not null ? $"{entry.Id}  {entry.Desc}" : $"Cannot find {v.Get("Entry")}");
        c.Add("Child disk exists", src != "" && File.Exists(src), src);
        foreach (var f in chain)
            c.Add($"Not mounted: {Path.GetFileName(f)}", File.Exists(f) && !Storage.IsAttached(f), !File.Exists(f) ? $"{f} does not exist (differencing chain is broken)" : "A mounted or running disk cannot be exported");
        c.Add("Differencing chain", chain.Count >= 1, $"{chain.Count} layers: {string.Join(" → ", chain.Select(Path.GetFileName))}");
        c.Add("Base disk has reserved ESP", facts?.HasEsp == true,
            facts is null ? "Cannot read child disk contents" : facts.HasEsp ? facts.Layout : $"{facts.Layout}: old layout, no FAT32 ESP at the start; redo with a new base disk");
        c.Add("Extra space", expand >= 0, expand > 0 ? $"Add {expand} GB; delete the recovery partition after the Windows partition, then extend the Windows partition" : "No extension");
        c.FileName("Name", name);
        c.Add("Target file does not exist", !File.Exists(outF), File.Exists(outF) ? $"{outF} already exists, pick another name" : outF);
        ctx.CheckMountRoot(c);
        if (virtio)
        {
            if (facts is null) c.Add("vioscsi", false, "Cannot read child disk contents, cannot tell", warn: true);
            else if (facts.HasVioscsi) c.Add("vioscsi", true, "Already in the child disk, no need to inject");
            else
            {
                var iso = ctx.Cfg.ResolveVirtioIso();
                var dir = iso != "" && File.Exists(iso) ? ctx.Mnt.MountIso(iso, "iso-virtio") : "";
                var miss = VirtioDrivers.Names.Where(d => !(dir != "" && VirtioDrivers.HasInf($@"{dir}\{d}\{vioOs}\amd64"))).ToList();
                c.Add("vioscsi", dir != "" && miss.Count == 0, dir == "" ? "Not in the child disk and virtio ISO not found" : miss.Count > 0 ? $"Not in the child disk; missing from the ISO: {string.Join(", ", miss)}" : $"Not in the child disk; injecting {VirtioDrivers.Names.Length} {vioOs} drivers from {iso}");
            }
        }
        else c.Add("vioscsi", true, "Not injected", skip: true);
        c.Add("Serial console", true, entry?.Ems == true ? "Original boot entry has EMS on, so the exported disk BCD turns it on too (COM1, 115200)" : "Original boot entry has EMS off");
        c.Dir("Export folder", ctx.Cfg.ExportDir);
        c.FreeSpace(ctx.Cfg.ExportDir, (long)Math.Ceiling((facts?.UsedGB ?? 30) + 5), "The exported disk is dynamically expanding, roughly equal to the used space", warn: true);
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var list = Candidates(ctx);
        var entry = Pick(list, v.Get("Entry")) ?? throw new WlbError("Boot entry not found");
        var src = entry.Vhd;
        var name = v.Get("Name").StripVhdx();
        var outF = ctx.Cfg.ExportPath(name);
        var expand = v.Int("ExpandGB");
        var virtio = v.Bool("Virtio", true);
        var chain = Context.Chain(src);
        var facts = GetFacts(ctx, src);
        var needVio = virtio && facts is not null && !facts.HasVioscsi;
        var vioOs = facts?.IsServer == true ? "2k25" : "w11";

        var outAttached = false;
        var bcdBak = Path.Combine(Path.GetTempPath(), $"wlb-export-bcd-{Guid.NewGuid():N}");
        var keepBak = false;
        try
        {
            Directory.CreateDirectory(ctx.Cfg.ExportDir);
            ctx.Stage("Merge differencing chain", $"{src} → {outF}");
            Storage.CreateFrom(outF, src, pct => ctx.Out.Progress("Merging", pct));

            if (expand > 0)
            {
                var newMB = (long)Math.Ceiling(Storage.DiskImage(outF)!.Size / (double)(1 << 20)) + expand * 1024L;
                ctx.Stage("Extra space", $"+{expand} GB (virtual disk → {Math.Round(newMB / 1024.0)} GB)");
                Storage.Resize(outF, (ulong)newMB << 20);
            }
            else ctx.Stage("Extra space", "No extension", skipped: true);

            ctx.Stage("Mount exported disk");
            var parts = ctx.Mnt.AttachVhd(outF, readOnly: false);
            outAttached = true;
            var win = ctx.Mnt.WindowsPart(parts, outF);
            if (expand > 0)
            {
                var disk = Storage.DiskNumber(outF);
                var tail = parts.Where(p => p.Type == "Recovery" && p.Offset > win.Offset).ToList();
                foreach (var r in tail)
                {
                    ctx.Say($"    Deleting recovery partition #{r.Number} after the Windows partition ({Files.MB(r.Size)} MB)");
                    DiskWmi.DeletePartition(disk, r.Number);
                }
                if (tail.Count == 0) ctx.Say("    No recovery partition after the Windows partition");
                DiskWmi.Extend(disk, win.Number);
                parts = Storage.PartitionsOf(outF);
                win = ctx.Mnt.WindowsPart(parts, outF);
            }
            var esp = parts.First(p => p.Type == "System");
            var espDir = ctx.Mnt.MountVolume("esp", esp.VolumeName);
            var winDir = ctx.Mnt.MountVolume("win", win.VolumeName);
            ctx.Say($"    Partitions: {string.Join(" → ", parts.Select(p => $"{p.Type} {Files.MB(p.Size)} MB"))}");
            ctx.Say($"    ESP → {espDir}, Windows → {winDir}");
            if (!File.Exists($@"{winDir}\Windows\System32\config\SYSTEM")) throw new WlbError($"Cannot find Windows in {winDir}");

            ctx.Stage("Drivers and boot files");
            if (needVio)
            {
                var iso = ctx.Cfg.ResolveVirtioIso();
                VirtioDrivers.Inject(ctx, winDir, ctx.Mnt.MountIso(iso, "iso-virtio"), vioOs);
            }
            else if (virtio) ctx.Say("  vioscsi is already in the child disk, no need to inject");
            // bcdboot /s should only write the BCD on the exported disk's ESP. Take a snapshot of the system BCD and export a backup first; after writing, compare item by item and restore any changed item one by one; the backup is only for manual comparison
            using var bcdLock = ctx.Bcd.Exclusive();   // The whole span from taking the snapshot to restoring must not be interleaved with anyone else's changes to the system BCD
            new BcdStore().Export(bcdBak);
            var snap = ctx.Bcd.Snapshot();
            var locale = ctx.Bcd.MenuLocale(winDir);
            try
            {
                ctx.Proc.Exec("bcdboot", "successfully|Failure", "bcdboot.exe", $@"{winDir}\Windows", "/s", espDir, "/f", "UEFI", "/l", locale);
                ctx.Say($"  Exported disk boot menu language {locale}");
            }
            finally
            {
                try { if (!ctx.Bcd.Restore(snap, [])) keepBak = true; }
                catch (Exception e) { keepBak = true; ctx.Say($"  BCD verification failed: {e.Message}", Level.Error); }
                if (keepBak) ctx.Say($"  Backup of the system BCD from before the run is kept at {bcdBak} (for comparison only, do not /import)", Level.Warn);
            }
            var espBcd = new BcdStore($@"{espDir}\EFI\Microsoft\Boot\BCD");
            var defId = espBcd.Elements(Bcd.BootmgrId)[BcdStore.DefaultObject];
            if (entry.Ems)
            {
                espBcd.Set(defId, BcdStore.Ems, "True");
                espBcd.Set(Bcd.BootmgrId, BcdStore.BootEms, "True");
                espBcd.Set(BcdStore.EmsSettingsId, BcdStore.EmsPort, "1");
                espBcd.Set(BcdStore.EmsSettingsId, BcdStore.EmsBaudRate, "115200");
                ctx.Say("  Exported disk BCD has the serial console on (COM1, 115200)");
            }
            var def = espBcd.Elements(defId);
            ctx.Say($"  Exported disk BCD: {def.GetValueOrDefault(BcdStore.Description)} | {def.GetValueOrDefault(BcdStore.Device)}{(def.GetValueOrDefault(BcdStore.Ems) == "True" ? " | ems" : "")}");

            ctx.Stage("Unmount exported disk");
            ctx.Mnt.DetachVhd(outF);
            outAttached = false;
            ctx.Say($"    Unmounted {outF}");

            ctx.Title("Export complete");
            ctx.Result("File", $"{outF} ({Files.GB1(new FileInfo(outF).Length)} GB, max {Math.Round((Storage.DiskImage(outF)?.Size ?? 0) / (double)(1L << 30))} GB)");
            ctx.Result("Source", $"{entry.Desc} ({src}, {chain.Count} layers of differencing chain)");
            ctx.Result("Copy to hypervisor host", $"scp <this PC>:{outF.Replace('\\', '/')} /var/lib/libvirt/images/");
            ctx.Result("Convert to qcow2", $"qemu-img convert -p -f vhdx -O qcow2 /var/lib/libvirt/images/{name}.vhdx /var/lib/libvirt/images/{name}.qcow2");
            ctx.Result("Resize later", $"Shut down, run qemu-img resize {name}.qcow2 +<N>G, then after boot run [Extend Volume] on C: in Disk Management");
            ctx.Result("Log", ctx.LogPath);
        }
        catch
        {
            ctx.Say($"Error. The exported disk {outF} is kept; find the cause, then delete it and rerun.", Level.Warn);
            throw;
        }
        finally
        {
            if (outAttached && Storage.IsAttached(outF)) ctx.Mnt.DetachVhd(outF);
            if (!keepBak) foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(bcdBak) + "*")) Files.TryDelete(f);
        }
    }
}
