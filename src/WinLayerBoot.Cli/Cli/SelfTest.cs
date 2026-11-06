using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinLayerBoot.Core;
using WinLayerBoot.Ops;

namespace WinLayerBoot.Cli;

/// <summary>
/// wlb selftest: verifies in the published exe that WMI and native APIs really work; problems that only appear after trimming are visible only this way, a Debug build cannot catch them.
/// Reads system state only (what WMI returns is compared with bcdedit); write operations are done on small files in a temp directory and deleted afterwards. Exit code 0 if all pass.
/// </summary>
internal static class SelfTest
{
    private const string StorageRoot = @"root\Microsoft\Windows\Storage";

    public static int Run(Context ctx)
    {
        var fails = 0;
        void T(string name, Func<string> body)
        {
            try { ctx.Say($"  OK     {name}: {body()}", Level.Ok); }
            catch (Exception e) { fails++; ctx.Say($"  Failed {name}: {e.GetType().Name}: {e.Message}", Level.Error); }
        }
        ctx.Title("Self-test");
        T("WMI BCD", () => WmiBcd(ctx));
        T("WMI storage", WmiStorage);
        T("WMI partitions", WmiPartitions);
        T("Virtual disk API", VirtualDisks);
        T("File permissions", Acl);
        T("WIM API", () => WimImages(ctx));
        T("Base disk language", () => BaseLanguage(ctx));
        T(".reg file", RegFiles);
        ctx.Say(fails == 0 ? "All passed" : $"{fails} failed", fails == 0 ? Level.Ok : Level.Error);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Compares the export with reg.exe verbatim, then imports it again and reads it back. Writes a temporary key under HKLM\SOFTWARE and deletes it afterwards.</summary>
    private static string RegFiles()
    {
        var root = $@"SOFTWARE\WLB-selftest-{Compat.ProcessId}";
        string a = $@"{root}\a", b = $@"{root}\b";
        var file = Path.GetTempFileName();
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey($@"{a}\subkey"))
            {
                k.SetValue("", "default value");
                k.SetValue("with \"quotes\" and \\backslash", @"C:\x ""y""");
                k.SetValue("empty", "");
                k.SetValue("dword", unchecked((int)0xFEDCBA98), RegistryValueKind.DWord);
                k.SetValue("qword", 0x0123456789ABCDEFL, RegistryValueKind.QWord);
                k.SetValue("expand", @"%SystemRoot%\x", RegistryValueKind.ExpandString);
                k.SetValue("multi", new[] { "a", "b c", "" + "d" }, RegistryValueKind.MultiString);
                k.SetValue("none", new byte[] { 1, 2 }, RegistryValueKind.None);
                k.SetValue("empty binary", Array.Empty<byte>(), RegistryValueKind.Binary);
                k.SetValue("ünïcode näme", Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(), RegistryValueKind.Binary);
                // Names of different lengths, so that every line-wrap position is covered
                for (var n = 1; n <= 12; n++) k.SetValue(new string('n', n), Enumerable.Range(0, 100 + n).Select(i => (byte)i).ToArray(), RegistryValueKind.Binary);
            }
            void SameAsReg(string path)
            {
                var (code, _) = Proc.Capture("reg.exe", "export", $@"HKLM\{path}", file, "/y");
                if (code != 0) throw new WlbError($"reg export {path} exit code {code}");
                var want = File.ReadAllText(file, Encoding.Unicode);
                var got = $"{RegFile.Header}\r\n\r\n{RegFile.Export(path)}";
                if (got == want) return;
                var i = Enumerable.Range(0, Math.Min(got.Length, want.Length)).Where(n => got[n] != want[n]).Append(Math.Min(got.Length, want.Length)).First();
                string Near(string s) => s.Substring(Math.Max(0, i - 40), Math.Min(s.Length - Math.Max(0, i - 40), 60));
                throw new WlbError($"{path} differs from reg.exe (at character {i}): reg.exe \"{Near(want)}\", here \"{Near(got)}\"");
            }
            SameAsReg(a);
            const string real = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration";
            if (Registry.LocalMachine.OpenSubKey(real) is { } r) { r.Dispose(); SameAsReg(real); }
            var text = RegFile.Export(a).Replace(a, b);
            RegFile.Import($"{RegFile.Header}\r\n\r\n{text}");
            if (RegFile.Export(b) != text) throw new WlbError("import then export differs from the original");
            SameAsReg(b);
            return "export matches reg.exe; import then export is unchanged";
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(root, false);
            Files.TryDelete(file);
        }
    }

    /// <summary>Compares the language read from the offline registry with dism /Get-Intl, once for each base disk.</summary>
    private static string BaseLanguage(Context ctx)
    {
        var bases = Directory.Exists(ctx.Cfg.VhdDir) ? Directory.GetFiles(ctx.Cfg.VhdDir, $"{Config.BasePrefix}*.vhdx") : [];
        foreach (var b in bases)
        {
            var i = ctx.BaseIntl(b) ?? throw new WlbError($"cannot read {b}");
            var txt = ctx.Mnt.WithWindows(b, readOnly: true, "base", (dir, _) =>
                string.Join("\n", Proc.Capture("dism.exe", "/English", $@"/Image:{dir}\", "/Get-Intl").Lines), quiet: true);
            void Same(string name, string pattern, string got)
            {
                var want = Regex.Match(txt, pattern).Groups[1].Value;
                if (want != got) throw new WlbError($"{name} of {Path.GetFileName(b)}: dism says \"{want}\", read \"{got}\"");
            }
            Same("UI language", @"Default system UI language : (\S+)", i.UI);
            Same("System locale", @"System locale : (\S+)", i.SysLocale);
            Same("User locale", @"User locale for default user : (\S+)", i.UserLocale);
            Same("Time zone", @"(?m)Default time zone : (.+?)\s*$", i.TimeZone);
            Same("Input method", @"Active keyboard\(s\) :\s*([^,\s]+)", i.Keyboard);
        }
        return bases.Length == 0 ? "Skipped (no base disks)" : $"{bases.Length} base disk(s) match dism";
    }

    /// <summary>Compares the boot entries read by WMI with the bcdedit output; element writes are tried on an exported copy of the BCD, the system BCD is not touched.</summary>
    private static string WmiBcd(Context ctx)
    {
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? cur = null;
        foreach (var l in Proc.Capture("bcdedit.exe", "/enum", "osloader", "/v").Lines)
        {
            var m = Regex.Match(l, @"^(\S+)\s+(.*)$");
            if (!m.Success) continue;
            if (m.Groups[1].Value.EqualsIgnoreCase("identifier")) fields[m.Groups[2].Value.Trim()] = cur = [];
            else cur?.TryAdd(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim());
        }
        var entries = ctx.Bcd.Entries();
        foreach (var e in entries)
        {
            if (!fields.TryGetValue(e.Id, out var f)) throw new WlbError($"{e.Id} not found in bcdedit");
            if (f.GetValueOrDefault("description") != e.Desc) throw new WlbError($"description of {e.Id}: bcdedit says \"{f.GetValueOrDefault("description")}\", WMI says \"{e.Desc}\"");
            if (f.GetValueOrDefault("locale", "") != e.Locale) throw new WlbError($"language of {e.Id}: bcdedit says \"{f.GetValueOrDefault("locale")}\", WMI says \"{e.Locale}\"");
            if (!f.GetValueOrDefault("device", "").Split(',')[0].EqualsIgnoreCase(e.VhdRaw.Split(',')[0])) throw new WlbError($"device of {e.Id}: bcdedit says \"{f.GetValueOrDefault("device")}\", WMI says \"{e.VhdRaw}\"");
        }
        var snap = ctx.Bcd.Snapshot();
        if (!snap.ContainsKey(Bcd.BootmgrId) || !snap.ContainsKey(Bcd.FwBootmgrId)) throw new WlbError("snapshot has no {bootmgr} or {fwbootmgr}");

        var copy = Path.Combine(Path.GetTempPath(), $"wlb-selftest-{Compat.ProcessId}.bcd");
        try
        {
            new BcdStore().Export(copy);
            var s = new BcdStore(copy);
            if (entries.Count > 0)
            {
                const string vhd = @"C:\wlb-selftest\x y.vhdx", want = @"vhd=[C:]\wlb-selftest\x y.vhdx";
                var c = s.Copy(entries[0].Id);
                s.SetVhd(c, vhd);
                var o = s.Elements(c);
                if (o.GetValueOrDefault(BcdStore.Device) != want || o.GetValueOrDefault(BcdStore.OsDevice) != want) throw new WlbError($"writing VHD device: read back \"{o.GetValueOrDefault(BcdStore.Device)}\"");
                s.DeleteObject(c);
                if (s.Objects(BcdStore.OsLoader).Contains(c, StringComparer.OrdinalIgnoreCase)) throw new WlbError("deleting the object had no effect");
            }
            var order = Enumerable.Reverse(snap[Bcd.BootmgrId].GetValueOrDefault(BcdStore.DisplayOrder, "").Split('\n')).ToArray();
            void RoundTrip(uint type, string value)
            {
                s.Set(Bcd.BootmgrId, type, value);
                var got = s.Elements(Bcd.BootmgrId).GetValueOrDefault(type);
                if (got != value) throw new WlbError($"writing {BcdStore.ElementName(type)}: wrote \"{value}\", read back \"{got}\"");
            }
            RoundTrip(BcdStore.Locale, "ja-JP");
            RoundTrip(BcdStore.Timeout, "17");
            RoundTrip(BcdStore.DisplayBootMenu, "True");
            RoundTrip(BcdStore.DisplayOrder, string.Join("\n", order));
            s.Delete(Bcd.BootmgrId, BcdStore.DisplayBootMenu);
            if (s.Elements(Bcd.BootmgrId).ContainsKey(BcdStore.DisplayBootMenu)) throw new WlbError("deleting the element had no effect");
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(copy) + "*")) Files.TryDelete(f); }
        return $"{entries.Count} boot entries match bcdedit, snapshot has {snap.Count} objects, write and delete work on the copy, {{bootmgr}} locale {snap[Bcd.BootmgrId].GetValueOrDefault(BcdStore.Locale)}";
    }

    private static string WmiStorage()
    {
        using var disks = new ManagementObjectSearcher(StorageRoot, "SELECT Number FROM MSFT_Disk");
        var diskCount = disks.Get().Count;
        using var parts = new ManagementObjectSearcher(StorageRoot, "SELECT DriveLetter, Size FROM MSFT_Partition");
        var all = parts.Get().Cast<ManagementObject>().ToList();
        var sys = Path.GetPathRoot(Environment.SystemDirectory)![0];
        if (diskCount == 0) throw new WlbError("no disks found");
        if (!all.Any(p => Convert.ToChar(p["DriveLetter"]) == sys)) throw new WlbError($"system partition {sys}: not found");
        return $"{diskCount} disk(s), {all.Count} partition(s), system partition {sys}:";
    }

    /// <summary>A small disk in the temp directory: initialize GPT, create ESP / MSR / Windows / recovery partitions, format, delete the recovery partition, extend the Windows partition.</summary>
    private static string WmiPartitions()
    {
        var dir = Compat.NewTempDir("wlb-selftest-");
        var vhd = Path.Combine(dir, "p.vhdx");
        var attached = false;
        try
        {
            Storage.CreateVhdx(vhd, 512UL << 20);
            Storage.Attach(vhd, readOnly: false);
            attached = true;
            var disk = Storage.DiskNumber(vhd);
            DiskWmi.InitializeGpt(disk);
            foreach (var p in Storage.PartitionsOf(vhd).Where(p => p.Type == "Reserved")) DiskWmi.DeletePartition(disk, p.Number);
            DiskWmi.Format(disk, DiskWmi.CreatePartition(disk, DiskWmi.EspType, 64UL << 20, offset: 1UL << 20), "FAT32", "System");
            DiskWmi.CreatePartition(disk, DiskWmi.MsrType, 16UL << 20);
            var win = DiskWmi.CreatePartition(disk, DiskWmi.BasicType, 128UL << 20);
            DiskWmi.Format(disk, win, "NTFS", "Win11");
            var recovery = DiskWmi.CreatePartition(disk, DiskWmi.RecoveryType);
            string Layout() => string.Join(" → ", Storage.PartitionsOf(vhd).Select(p => $"{p.Type} {Files.MB(p.Size)}"));
            var before = Storage.PartitionsOf(vhd).First(p => p.Number == win).Size;
            var first = Layout();
            DiskWmi.DeletePartition(disk, recovery);
            DiskWmi.Extend(disk, win);
            var after = Storage.PartitionsOf(vhd);
            if (after.Count != 3 || after[0].Type != "System" || after[1].Type != "Reserved" || after[2].Type != "Basic") throw new WlbError($"wrong partition layout: {Layout()}");
            if (after[2].Size <= before) throw new WlbError($"not enlarged: {Layout()}");
            return $"{first}, after deleting the recovery partition and enlarging {Layout()} (MB)";
        }
        finally
        {
            if (attached) Storage.Detach(vhd);
            Files.TryDelete(dir);
        }
    }

    /// <summary>In the temp directory: create a dynamic disk, extend it, create a differencing disk, merge the differencing disk into a standalone disk.</summary>
    private static string VirtualDisks()
    {
        var dir = Compat.NewTempDir("wlb-selftest-");
        try
        {
            string a = Path.Combine(dir, "a.vhdx"), b = Path.Combine(dir, "b.vhdx"), c = Path.Combine(dir, "c.vhdx");
            const ulong size = 2UL << 30;
            Storage.CreateVhdx(a, size / 2);
            Storage.Resize(a, size);
            if (VhdxInfo.Read(a)?.VirtualSize != size) throw new WlbError("wrong size after enlarging");
            Storage.CreateDifferencing(b, a);
            if (VhdxInfo.Read(b) is not { HasParent: true }) throw new WlbError("differencing disk has no parent disk");
            var last = -1;
            Storage.CreateFrom(c, b, p => last = p);
            if (VhdxInfo.Read(c) is not { HasParent: false, VirtualSize: size }) throw new WlbError("merged disk is not standalone, or its size is wrong");
            return $"create, enlarge, differencing and merge all work (last merge progress {last}%)";
        }
        finally { Files.TryDelete(dir); }
    }

    private static string Acl()
    {
        var f = Path.GetTempFileName();
        try
        {
            Files.RestrictToAdmins(f);
            var sddl = new FileInfo(f).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            const string want = "D:PAI(A;;FA;;;SY)(A;;FA;;;BA)";
            return sddl == want ? sddl : throw new WlbError($"got {sddl}, expected {want}");
        }
        finally { Files.TryDelete(f); }
    }

    /// <summary>Mounts the install ISO; the image count from wimgapi and from reading the file header directly must be the same. Skipped when there is no ISO.</summary>
    private static string WimImages(Context ctx)
    {
        var iso = ctx.Cfg.FindInstallIso();
        if (iso == "") return "Skipped (no install ISO in the iso folder)";
        var wim = Path.Combine(ctx.Mnt.MountIso(iso, "iso-install"), "sources", "install.wim");
        var n = Wim.ImageCount(wim);
        var want = BaseOp.ReadWim(wim).Count;
        return n == want ? $"{n} image(s) in {Path.GetFileName(iso)}" : throw new WlbError($"wimgapi read {n}, file header says {want}");
    }
}
