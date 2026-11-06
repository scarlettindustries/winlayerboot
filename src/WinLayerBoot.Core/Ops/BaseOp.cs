using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinLayerBoot.Core;

namespace WinLayerBoot.Ops;

/// <summary>Builds a base disk from an installation ISO: three partitions, empty ESP → MSR → Windows; applies install.wim and sets it read-only.</summary>
public sealed class BaseOp : Operation
{
    public override string Name => "base";
    public override string Title => "Create base disk";
    public override string Summary => "Unpack Windows from the install ISO into a new VHDX and set it read-only; all child disks derive from it";
    public override string[] Steps =>
    [
        "Create VHDX (GPT: empty ESP → MSR 16 MB → Windows); the ESP is left for boot files written when exporting as a standalone disk later",
        "Unpack the selected edition from install.wim",
        "Read the registry offline to verify the edition",
        "Unmount, set read-only, optionally compute SHA256 into <name>.vhdx.sha256",
    ];

    public override string[] Stages => ["Create VHDX", "Unpacking image", "Verify edition", "Unmount, set read-only"];
    public override IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>
    {
        ["Install ISO"] = "Iso", ["install.wim"] = "Iso", ["Edition exists"] = "Edition", ["Base disk name"] = "Name", ["New base disk file name available"] = "Name",
        ["Max size"] = "MaxGB", ["Reserved ESP size"] = "EspMB",
    };

    public sealed class WimImage
    {
        public int Index;
        public string EditionId = "", Name = "", Version = "", Languages = "";
        public string Label => $"{Index}: {EditionId} ({Name}, {Version}, {Languages})";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<WimImage>> _wimCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>After mounting the installation ISO, reads the edition list from install.wim (cached). Empty if the ISO does not exist or has no install.wim.</summary>
    private List<WimImage> Images(Context ctx, string iso, out string wim)
    {
        wim = "";
        if (iso == "" || !File.Exists(iso)) return [];
        if (_wimCache.TryGetValue(iso, out var c) && c.Count > 0) { wim = Path.Combine(ctx.Mnt.MountIso(iso, "iso-install"), "sources", "install.wim"); return c; }
        wim = Path.Combine(ctx.Mnt.MountIso(iso, "iso-install"), "sources", "install.wim");
        var list = File.Exists(wim) ? ReadWim(wim) : [];
        _wimCache[iso] = list;
        return list;
    }

    /// <summary>
    /// Replacement for Get-WindowsImage: reads the XML metadata directly from the WIM file header.
    /// At 0x48 in the header is the XML resource header (7-byte length + 1-byte flags, 8-byte offset, 8-byte original length); the XML is UTF-16.
    /// </summary>
    public static List<WimImage> ReadWim(string wim)
    {
        List<WimImage> list = [];
        using var fs = File.OpenRead(wim);
        var head = new byte[0x60];
        if (fs.Read(head, 0, head.Length) != head.Length || Encoding.ASCII.GetString(head, 0, 5) != "MSWIM") return list;
        var size = BitConverter.ToInt64(head, 0x48) & 0x00FFFFFFFFFFFFFF;
        var offset = BitConverter.ToInt64(head, 0x50);
        var buf = new byte[size];
        fs.Seek(offset, SeekOrigin.Begin);
        Compat.ReadAll(fs, buf);
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(Encoding.Unicode.GetString(buf).TrimStart('﻿'));
        foreach (System.Xml.XmlElement img in doc.SelectNodes("/WIM/IMAGE")!)
        {
            string T(string xpath) => img.SelectSingleNode(xpath)?.InnerText.Trim() ?? "";
            var ver = string.Join(".", new[] { T("WINDOWS/VERSION/MAJOR"), T("WINDOWS/VERSION/MINOR"), T("WINDOWS/VERSION/BUILD"), T("WINDOWS/VERSION/SPBUILD") }.Where(x => x != ""));
            list.Add(new WimImage
            {
                Index = int.Parse(img.GetAttribute("INDEX")),
                EditionId = T("WINDOWS/EDITIONID"),
                Name = T("DISPLAYNAME") != "" ? T("DISPLAYNAME") : T("NAME"),
                Version = ver,
                Languages = string.Join(",", img.SelectNodes("WINDOWS/LANGUAGES/LANGUAGE")!.Cast<System.Xml.XmlNode>().Select(n => n.InnerText.Trim())),
            });
        }
        return list.OrderBy(i => i.Index).ToList();
    }

    /// <summary>Default base disk name: &lt;product&gt;-&lt;build&gt;-&lt;edition abbreviation&gt;-&lt;language&gt;, for example win11-26200-pws-en-us.</summary>
    private static string DefaultName(WimImage? img)
    {
        if (img is null) return "win11-base";
        Match m;
        var prod = (m = Regex.Match(img.Name, @"Server\D*(\d{4})", RegexOptions.IgnoreCase)).Success ? $"ws{m.Groups[1].Value}"
            : (m = Regex.Match(img.Name, @"Windows\D*(\d+)", RegexOptions.IgnoreCase)).Success ? $"win{m.Groups[1].Value}" : "win";
        var parts = img.Version.Split('.');
        var build = parts.Length > 2 ? parts[2] : "";
        var lang = img.Languages.Split(',')[0].ToLowerInvariant();
        return string.Join("-", new[] { prod, build, Context.EditionShort(img.EditionId), lang }.Where(x => x != ""));
    }

    // A Server EditionId maps to two images (Core and Desktop Experience); selecting by EditionId must pick Desktop Experience; Core is selected by index or full name
    private static WimImage? Pick(List<WimImage> images, string edition) =>
        int.TryParse(edition, out var n) ? images.FirstOrDefault(i => i.Index == n)
        : images.Where(i => i.EditionId.EqualsIgnoreCase(edition)).OrderByDescending(i => i.Name.Contains("Desktop Experience", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
          ?? images.FirstOrDefault(i => i.Name.EqualsIgnoreCase(edition));

    public override IReadOnlyList<Field> Fields(Context ctx, Values v)
    {
        var defIso = ctx.Cfg.FindInstallIso();
        var iso = v.Has("Iso") ? v.Get("Iso") : defIso;
        var images = Images(ctx, iso, out _);
        var pick = Pick(images, v.Get("Edition", "ProfessionalWorkstation"));
        var defName = Config.BasePrefix + DefaultName(pick);
        return
        [
            new Field("Iso", "Install ISO", FieldKind.Choice, defIso)
            {
                Choices = [.. ctx.Cfg.AllIsos()],
                Help = $"Full path of the Windows install ISO; by default searched in {ctx.Cfg.IsoDir} for *windows_11* and *windows_server* (excluding *LOF*)",
            },
            new Field("Edition", "Edition", FieldKind.Choice, images.Any(i => i.EditionId == "ProfessionalWorkstation") ? "ProfessionalWorkstation" : images.FirstOrDefault()?.EditionId ?? "ProfessionalWorkstation")
            {
                Choices = images.Select(i => i.EditionId).Distinct().ToArray(),
                Help = images.Count > 0 ? "Editions in install.wim: " + string.Join("; ", images.Select(i => i.Label)) : "Enter EditionId or ImageIndex",
            },
            new Field("Name", "Base disk name", FieldKind.Text, defName) { Help = $"File is vhd\\<name>.vhdx; generated from the selected edition, editable; the {Config.BasePrefix} prefix is added automatically if missing" },
            new Field("MaxGB", "Max size GB", FieldKind.Int, "64") { Help = "Dynamically expanding, actual usage about 16 GB" },
            new Field("EspMB", "Reserved ESP size MB", FieldKind.Int, "1024") { Help = "Empty FAT32 partition at the front, boot files are written into it when exporting as a standalone disk; cannot be changed after the base disk is created", Advanced = true },
            new Field("WriteHash", "Write SHA256 when done", FieldKind.Bool, "N") { Help = "About 1 minute; lets you verify the base disk was not modified when creating child disks" },
        ];
    }

    private static string FullName(Values v)
    {
        var name = v.Get("Name").StripVhdx();
        return name.StartsWith(Config.BasePrefix, StringComparison.OrdinalIgnoreCase) ? name : Config.BasePrefix + name;
    }

    public override CheckList Check(Context ctx, Values v)
    {
        var c = new CheckList();
        var iso = v.Get("Iso");
        var images = Images(ctx, iso, out var wim);
        var pick = Pick(images, v.Get("Edition"));
        var name = FullName(v);
        var vhdx = ctx.Cfg.VhdPath(name);
        var maxGB = v.Int("MaxGB");
        var espMB = v.Int("EspMB");
        c.Admin();
        c.Add("Install ISO", iso != "" && File.Exists(iso), iso != "" ? iso : $"Not set, and none found in {ctx.Cfg.IsoDir} either");
        c.Add("install.wim", wim != "" && File.Exists(wim), wim != "" ? wim : "");
        c.Add("Edition exists", pick is not null, pick is not null ? pick.Label : $"No {v.Get("Edition")} in install.wim");
        c.Add("Base disk name", name != Config.BasePrefix && Files.IsValidName(name), name);
        c.Add("Max size", maxGB >= 32, $"{maxGB} GB (about 16 GB after unpacking, plus room for child disk writes)");
        c.Add("Reserved ESP size", espMB >= 100 && espMB <= 4096, $"{espMB} MB (100–4096)");
        c.Dir("VHDX folder", ctx.Cfg.VhdDir);
        c.NewFile("New base disk file name available", vhdx, "use another name or delete it first");
        ctx.CheckMountRoot(c);
        c.FreeSpace(ctx.Cfg.VhdDir, 25, "about 17 GB after unpacking");
        return c;
    }

    public override void Run(Context ctx, Values v)
    {
        var iso = v.Get("Iso");
        var images = Images(ctx, iso, out var wim);
        var pick = Pick(images, v.Get("Edition")) ?? throw new WlbError("Edition not found");
        var name = FullName(v);
        var vhdx = ctx.Cfg.VhdPath(name);
        var maxGB = v.Int("MaxGB");
        var espMB = v.Int("EspMB");
        var attached = false;
        string? win = null;
        try
        {
            Directory.CreateDirectory(ctx.Cfg.VhdDir);
            ctx.Stage("Create VHDX", $"{vhdx} (GPT: empty ESP {espMB} MB → MSR 16 MB → Windows)");
            Storage.CreateVhdx(vhdx, (ulong)maxGB << 30);
            uint disk;
            using (var dw = new Storage.VolumeWatcher(disk: true))
            {
                Storage.Attach(vhdx, readOnly: false);
                attached = true;
                disk = Storage.DiskNumber(vhdx);
                var td = dw.Until(() => Storage.DiskReady(disk), Mounts.VolumeTimeout, "disk device of the new disk");
                ctx.Say($"    Mounted as disk {disk}, device ready in {td.TotalMilliseconds:0} ms");
            }
            DiskWmi.InitializeGpt(disk);
            // When Server 2025 initializes GPT it automatically creates a 16 MB MSR at the very front; delete it first so the ESP can be first;
            // pin the ESP at 1 MB, otherwise the ESP does not fit in the gap and ends up later
            foreach (var p in Storage.PartitionsOf(vhdx).Where(p => p.Type == "Reserved")) DiskWmi.DeletePartition(disk, p.Number);
            List<Storage.Part> parts = [];
            using (var w = new Storage.VolumeWatcher())
            {
                DiskWmi.Format(disk, DiskWmi.CreatePartition(disk, DiskWmi.EspType, (ulong)espMB << 20, offset: 1UL << 20), "FAT32", "System");
                DiskWmi.CreatePartition(disk, DiskWmi.MsrType, 16UL << 20);
                DiskWmi.Format(disk, DiskWmi.CreatePartition(disk, DiskWmi.BasicType), "NTFS", "Win11");
                var t0 = w.Until(() => { parts = Storage.PartitionsOf(vhdx); return parts.Any(p => p.Type == "System" && p.VolumeName != "") && parts.Any(p => p.Type == "Basic" && p.VolumeName != ""); },
                    Mounts.VolumeTimeout, "volume of the new partition");
                ctx.Say($"    Volume of the new partition ready in {t0.TotalMilliseconds:0} ms");
            }
            foreach (var p in parts.Where(p => p.Letter != '\0')) { ctx.Say($"    Removing the drive letter assigned by the system {p.Letter}:"); Storage.RemoveLetter(p); }
            ctx.Say($"    Partitions: {string.Join(" → ", parts.Select(p => $"{p.Type} {Files.MB(p.Size)} MB"))}");
            if (parts.Count != 3 || parts[0].Type != "System" || parts[1].Type != "Reserved" || parts[2].Type != "Basic")
                throw new WlbError("The base disk partition layout is wrong, expected ESP → MSR → Windows");

            win = ctx.Mnt.MountVolume("win", parts[2].VolumeName);
            ctx.Stage("Unpacking image", $@"index {pick.Index} ({pick.EditionId}) → {win}\");
            var t = DateTime.Now;
            Wim.Apply(wim, pick.Index, win, pct => ctx.Out.Progress("Unpacking image", pct));
            ctx.Say($"    Took {(int)(DateTime.Now - t).TotalSeconds} s");

            ctx.Stage("Verify edition", "Reading the registry offline");
            var hive = ctx.Hives.Load("S1Soft", $@"{win}\Windows\System32\config\SOFTWARE");
            string ed, dv, cb, ubr;
            using (var k = Registry.LocalMachine.OpenSubKey($@"{hive}\Microsoft\Windows NT\CurrentVersion"))
            {
                ed = k?.GetValue("EditionID") as string ?? "";
                dv = k?.GetValue("DisplayVersion") as string ?? "";
                cb = k?.GetValue("CurrentBuild") as string ?? "";
                ubr = k?.GetValue("UBR")?.ToString() ?? "";
            }
            ctx.Hives.UnloadAll();
            ctx.Say($"    EditionID {ed}, DisplayVersion {dv}, CurrentBuild {cb}.{ubr}");
            if (!ed.EqualsIgnoreCase(pick.EditionId)) throw new WlbError($"EditionID is {ed}, not {pick.EditionId}");

            var writeHash = v.Bool("WriteHash");
            ctx.Stage("Unmount, set read-only", writeHash ? "Recording hash" : "");
            ctx.Mnt.UnmountDir(win);
            ctx.Mnt.DetachVhd(vhdx);
            ctx.Say($"    Unmounted {vhdx}");
            attached = false;
            File.SetAttributes(vhdx, File.GetAttributes(vhdx) | FileAttributes.ReadOnly);
            string? hash = null;
            if (writeHash)
            {
                ctx.Say("    Computing SHA256...");
                hash = Files.Sha256(vhdx);
                Files.WriteText(vhdx + ".sha256", hash, Encoding.ASCII);
            }
            else ctx.Say("    Skipping SHA256");

            ctx.Title("Base disk done");
            var fi = new FileInfo(vhdx);
            ctx.Result("Base disk", $"{vhdx} ({Files.GB1(fi.Length)} GB, {fi.Attributes})");
            if (hash is not null) ctx.Result("SHA256", hash);
            ctx.Result("Log", ctx.LogPath);
        }
        catch
        {
            ctx.Say($"The VHDX is kept at {vhdx}; delete it manually once you have confirmed the cause.", Level.Warn);
            throw;
        }
        finally
        {
            ctx.Hives.UnloadAll();
            if (attached && Storage.IsAttached(vhdx)) { ctx.Mnt.UnmountDir(win); ctx.Mnt.DetachVhd(vhdx); }
        }
    }
}
