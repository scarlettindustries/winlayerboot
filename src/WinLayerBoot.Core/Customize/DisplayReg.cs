using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinLayerBoot.Customize;

/// <summary>Organizing and reading display.reg: shared by saving display settings and restoring display settings.</summary>
public static class DisplayReg
{
    private sealed class RegBlock
    {
        public string Path = "", Text = "";
    }

    private sealed class CfgGroup
    {
        public string Path = "", Key = "", Short = "";
        public string[] Ids = [];
        public long Ts;
    }

    /// <summary>
    /// For the same set of monitors, Windows stores one Configuration per detection order (SetId = monitor numbers in order).
    /// Only the most recently used one is a well-arranged layout; other orders may still hold old layouts, and the old layout gets applied when the detection order changes at boot.
    /// Here, matching by monitor number, the target subtree of the most recently used one is copied to the other orders.
    /// "Most recently used" is taken from Recent in Connectivity for this set of monitors (pointing at that SetId), falling back to Timestamp only if absent:
    /// the Windows clock may differ by several hours between boots (on real machines the time is not yet synced at boot), so Timestamp cannot be compared across boots.
    /// </summary>
    public static (string Text, List<string> Fixed) Normalize(string text)
    {
        const string cfg = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration\";
        const string conn = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Connectivity\";
        var parts = Regex.Split(text, @"\r?\n(?=\[)");
        List<RegBlock> blocks = [];
        foreach (var raw in parts.Skip(1))
        {
            var p = raw.TrimEnd();
            var m = Regex.Match(p, @"^\[([^\]\r\n]+)\]");
            if (m.Success) blocks.Add(new RegBlock { Path = m.Groups[1].Value, Text = p });
        }
        List<CfgGroup> groups = [];
        foreach (var b in blocks)
        {
            if (!b.Path.StartsWith(cfg, StringComparison.Ordinal) || b.Path.Substring(cfg.Length).Contains('\\')) continue;
            var sm = Regex.Match(b.Text, "(?m)^\"SetId\"=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!sm.Success) continue;
            var ids = sm.Groups[1].Value.Split('+');
            long ts = 0;
            var tm = Regex.Match(b.Text, @"(?m)^""Timestamp""=hex\(b\):([0-9a-fA-F,]+)", RegexOptions.IgnoreCase);
            if (tm.Success)
            {
                var bytes = tm.Groups[1].Value.Split([','], StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();
                if (bytes.Length >= 8) ts = BitConverter.ToInt64(bytes, 0);
            }
            var tail = Regex.Replace(b.Path, @"^.*\^", "");
            groups.Add(new CfgGroup
            {
                Path = b.Path, Ids = ids, Ts = ts,
                Key = string.Join("+", ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                Short = tail.Length >= 8 ? tail.Substring(0, 8) : tail,
            });
        }
        var recent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blocks)
        {
            if (!b.Path.StartsWith(conn, StringComparison.OrdinalIgnoreCase)) continue;
            var rm = Regex.Match(b.Text, "(?m)^\"Recent\"=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (rm.Success) recent[string.Join("+", rm.Groups[1].Value.Split('+').OrderBy(x => x, StringComparer.OrdinalIgnoreCase))] = rm.Groups[1].Value;
        }
        List<string> Targets(string g) => blocks.Where(b => b.Path.StartsWith(g + @"\", StringComparison.Ordinal))
            .Select(b => b.Path.Substring(g.Length + 1).Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var repl = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string> fixedList = [];
        foreach (var set in groups.Where(g => g.Ids.Length > 1).GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Where(s => s.Count() > 1))
        {
            recent.TryGetValue(set.Key, out var want);
            var refG = set.FirstOrDefault(g => want is not null && string.Equals(string.Join("+", g.Ids), want, StringComparison.OrdinalIgnoreCase))
                       ?? set.OrderByDescending(g => g.Ts).First();
            var rt = Targets(refG.Path);
            if (refG.Ids.Distinct().Count() != refG.Ids.Length || rt.Count != refG.Ids.Length) continue;
            foreach (var g in set.Where(g => !string.Equals(g.Path, refG.Path, StringComparison.OrdinalIgnoreCase)))
            {
                var gt = Targets(g.Path);
                if (gt.Count != g.Ids.Length) continue;
                List<string> lines = [];
                for (var i = 0; i < gt.Count; i++)
                {
                    var ri = Array.IndexOf(refG.Ids, g.Ids[i]);
                    var src = $@"{refG.Path}\{rt[ri >= 0 ? ri : rt.Count - 1]}";
                    var dst = $@"{g.Path}\{gt[i]}";
                    foreach (var b in blocks.Where(b => string.Equals(b.Path, src, StringComparison.OrdinalIgnoreCase) || b.Path.StartsWith(src + @"\", StringComparison.Ordinal)))
                        lines.Add($"[{dst}{b.Path.Substring(src.Length)}]" + b.Text.Substring(b.Path.Length + 2));
                }
                repl[g.Path] = lines;
                fixedList.Add($"{g.Short} ← {refG.Short}");
            }
        }
        List<string> outList = [];
        foreach (var b in blocks)
        {
            if (repl.Keys.Any(k => b.Path.StartsWith(k + @"\", StringComparison.Ordinal))) continue;   // Discard the old target subtree
            outList.Add(b.Text);
            if (repl.TryGetValue(b.Path, out var r)) outList.AddRange(r);
        }
        return (parts[0].TrimEnd() + "\r\n\r\n" + string.Join("\r\n\r\n", outList) + "\r\n", fixedList);
    }

    public static readonly string[] LayoutHeaders = ["Display", "Resolution", "Position", "Refresh rate", "Rotation", "HDR", "Primary display"];

    /// <summary>
    /// The most recently used configuration of the set with the most monitors (the full layout when all are connected): per monitor its number, resolution, position, refresh rate, rotation, HDR.
    /// sysRoot is the name under HKLM of the loaded SYSTEM hive.
    /// </summary>
    public static List<string[]> ActiveLayout(string sysRoot)
    {
        var gd = $@"{sysRoot}\ControlSet001\Control\GraphicsDrivers";
        List<string[]> rows = [];
        var hklm = Registry.LocalMachine;
        string? want = null;
        using (var connK = hklm.OpenSubKey($@"{gd}\Connectivity"))
        {
            foreach (var n in connK?.GetSubKeyNames() ?? [])
            {
                using var c = connK!.OpenSubKey(n);
                var r = c?.GetValue("Recent") as string;
                if (r is not null && (want is null || r.Split('+').Length > want.Split('+').Length)) want = r;
            }
        }
        string? best = null;
        long bestTs = -1;
        using (var cfgK = hklm.OpenSubKey($@"{gd}\Configuration"))
        {
            foreach (var n in cfgK?.GetSubKeyNames() ?? [])
            {
                using var g = cfgK!.OpenSubKey(n);
                var sid = g?.GetValue("SetId") as string;
                var ts = g?.GetValue("Timestamp");
                if (want is not null && string.Equals(sid, want, StringComparison.OrdinalIgnoreCase)) { best = n; break; }
                if (want is null && sid is not null && ts is not null && Convert.ToInt64(ts) > bestTs) { best = n; bestTs = Convert.ToInt64(ts); }
            }
        }
        if (best is null) return rows;
        using var bk = hklm.OpenSubKey($@"{gd}\Configuration\{best}")!;
        var ids = ((bk.GetValue("SetId") as string) ?? "").Split('+');
        using var mds = hklm.OpenSubKey($@"{gd}\MonitorDataStore");
        var mdsNames = mds?.GetSubKeyNames() ?? [];
        var i = 0;
        foreach (var tn in bk.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            using var t = bk.OpenSubKey(tn)!;
            using var m = t.OpenSubKey("00");
            var id = i < ids.Length ? ids[i] : "";
            i++;
            var hdrName = mdsNames.FirstOrDefault(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))
                          ?? mdsNames.Where(x => id.StartsWith(x + "_", StringComparison.Ordinal)).OrderByDescending(x => x.Length).FirstOrDefault();
            using var hdr = hdrName is null ? null : mds!.OpenSubKey(hdrName);
            int Dw(RegistryKey? k, string v) => k?.GetValue(v) is int x ? x : 0;
            var x = Dw(t, "Position.cx");
            var y = Dw(t, "Position.cy");
            var den = Dw(m, "VSyncFreq.Denominator");
            var rot = Dw(m, "Rotation");
            rows.Add(
            [
                id,
                $"{Dw(t, "PrimSurfSize.cx")}x{Dw(t, "PrimSurfSize.cy")}",
                $"{x},{y}",
                m is not null && den != 0 ? $"{(double)(uint)Dw(m, "VSyncFreq.Numerator") / den:0.##} Hz" : "",
                m is not null ? rot switch { 1 => "0°", 2 => "90°", 3 => "180°", 4 => "270°", _ => "" } : "",
                Dw(hdr, "HDREnabled") == 1 ? "On" : "",
                x == 0 && y == 0 ? "Yes" : "",
            ]);
        }
        return rows;
    }
}
