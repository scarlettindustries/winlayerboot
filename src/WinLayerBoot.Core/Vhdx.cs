using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinLayerBoot.Core;

/// <summary>
/// Reads the VHDX file header directly, without mounting or running diskpart: virtual size, whether there is a parent disk, parent path,
/// parent linkage GUID (tells whether the parent was written after the child was created), log GUID (whether the last unmount was clean).
/// Layout: 1 MB header area = file identifier (0), two headers (64 KB, 128 KB), two region tables (192 KB, 256 KB); the metadata region is registered in the region table.
/// </summary>
public sealed class VhdxInfo
{
    public ulong VirtualSize;
    public bool HasParent;
    /// <summary>Parent path: absolute_win32_path if present, otherwise built from the relative path; empty if it cannot be resolved.</summary>
    public string ParentPath = "";
    public string ParentRelative = "";
    /// <summary>parent_linkage: the parent's DataWriteGuid when the child disk was created.</summary>
    public Guid ParentLinkage;
    /// <summary>Changes on every open-for-write; the child disk remembers the parent's value at that time, and a mismatch means the parent was written afterwards.</summary>
    public Guid DataWriteGuid;
    /// <summary>Non-empty means there is an unreplayed log (booted natively, not cleanly unmounted).</summary>
    public Guid LogGuid;
    public uint BlockSize;

    private static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid FileParameters = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
    private static readonly Guid VirtualDiskSize = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
    private static readonly Guid ParentLocator = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");

    public static VhdxInfo? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var sig = new byte[8];
            Compat.ReadAll(fs, sig);
            if (Encoding.ASCII.GetString(sig) != "vhdxfile") return null;
            var info = new VhdxInfo();

            // Two headers; take the one with the larger sequence number
            ulong bestSeq = 0;
            foreach (var off in new long[] { 0x10000, 0x20000 })
            {
                var h = ReadAt(fs, off, 128);
                if (Encoding.ASCII.GetString(h, 0, 4) != "head") continue;
                var seq = BitConverter.ToUInt64(h, 8);
                if (seq < bestSeq && bestSeq != 0) continue;
                bestSeq = seq;
                info.DataWriteGuid = Compat.GuidAt(h, 32);
                info.LogGuid = Compat.GuidAt(h, 48);
            }

            // Region table
            long metaOff = 0;
            uint metaLen = 0;
            foreach (var off in new long[] { 0x30000, 0x40000 })
            {
                var r = ReadAt(fs, off, 64 * 1024);
                if (Encoding.ASCII.GetString(r, 0, 4) != "regi") continue;
                var count = BitConverter.ToInt32(r, 8);
                for (var i = 0; i < Math.Min(count, 2047); i++)
                {
                    var e = 16 + i * 32;
                    if (Compat.GuidAt(r, e) != MetadataRegion) continue;
                    metaOff = BitConverter.ToInt64(r, e + 16);
                    metaLen = BitConverter.ToUInt32(r, e + 24);
                }
                if (metaOff != 0) break;
            }
            if (metaOff == 0) return info;

            var m = ReadAt(fs, metaOff, (int)Math.Min(metaLen, 1024 * 1024));
            if (Encoding.ASCII.GetString(m, 0, 8) != "metadata") return info;
            var n = BitConverter.ToUInt16(m, 10);
            for (var i = 0; i < n; i++)
            {
                var e = 32 + i * 32;
                var id = Compat.GuidAt(m, e);
                var off = BitConverter.ToInt32(m, e + 16);
                var len = BitConverter.ToInt32(m, e + 20);
                if (off <= 0 || len <= 0 || off + len > m.Length) continue;
                if (id == FileParameters)
                {
                    info.BlockSize = BitConverter.ToUInt32(m, off);
                    info.HasParent = (BitConverter.ToUInt32(m, off + 4) & 2) != 0;
                }
                else if (id == VirtualDiskSize) info.VirtualSize = BitConverter.ToUInt64(m, off);
                else if (id == ParentLocator)
                {
                    var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var cnt = BitConverter.ToUInt16(m, off + 18);
                    for (var k = 0; k < cnt; k++)
                    {
                        var ke = off + 20 + k * 12;
                        var ko = BitConverter.ToInt32(m, ke);
                        var vo = BitConverter.ToInt32(m, ke + 4);
                        var kl = BitConverter.ToUInt16(m, ke + 8);
                        var vl = BitConverter.ToUInt16(m, ke + 10);
                        if (off + ko + kl > m.Length || off + vo + vl > m.Length) continue;
                        kv[Encoding.Unicode.GetString(m, off + ko, kl)] = Encoding.Unicode.GetString(m, off + vo, vl);
                    }
                    if (kv.TryGetValue("parent_linkage", out var pl) && Guid.TryParse(pl, out var g)) info.ParentLinkage = g;
                    kv.TryGetValue("relative_path", out var rel);
                    kv.TryGetValue("absolute_win32_path", out var abs);
                    info.ParentRelative = rel ?? "";
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
                    string? cand = null;
                    if (abs is not null)
                    {
                        var a = abs.StartsWith(@"\\?\", StringComparison.Ordinal) ? abs.Substring(4) : abs;
                        if (File.Exists(a)) cand = a;
                    }
                    if (cand is null && rel is not null)
                    {
                        var r = Path.GetFullPath(Path.Combine(dir, rel.TrimStart('.', '\\')));
                        if (File.Exists(r)) cand = r;
                        else
                        {
                            var r2 = Path.GetFullPath(Path.Combine(dir, rel));
                            if (File.Exists(r2)) cand = r2;
                        }
                    }
                    info.ParentPath = cand ?? (abs is not null ? abs.Replace(@"\\?\", "") : rel ?? "");
                }
            }
            return info;
        }
        catch { return null; }
    }

    private static byte[] ReadAt(FileStream fs, long offset, int len)
    {
        var b = new byte[len];
        fs.Seek(offset, SeekOrigin.Begin);
        var n = 0;
        while (n < len)
        {
            var r = fs.Read(b, n, len - n);
            if (r <= 0) break;
            n += r;
        }
        return b;
    }
}
