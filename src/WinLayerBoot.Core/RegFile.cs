using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinLayerBoot.Core;

/// <summary>Export and import of .reg files (same format as reg.exe); handles only keys under HKLM, where offline hives are all loaded.</summary>
public static class RegFile
{
    public const string Header = "Windows Registry Editor Version 5.00";
    private const string Hklm = @"HKEY_LOCAL_MACHINE\";

    /// <summary>Exports this key and all its subkeys, one section per key. No file header.</summary>
    public static string Export(string path)
    {
        using var k = Registry.LocalMachine.OpenSubKey(path) ?? throw new WlbError($"Registry key does not exist: {path}");
        var sb = new StringBuilder();
        Write(sb, Hklm + path, k);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, string path, RegistryKey k)
    {
        sb.Append($"[{path}]\r\n");
        foreach (var name in k.GetValueNames()) sb.Append(Value(name, k)).Append("\r\n");
        sb.Append("\r\n");
        foreach (var sub in k.GetSubKeyNames())
        {
            using var c = k.OpenSubKey(sub) ?? throw new WlbError($@"Cannot open registry key: {path}\{sub}");
            Write(sb, $@"{path}\{sub}", c);
        }
    }

    private static string Value(string name, RegistryKey k)
    {
        var left = (name == "" ? "@" : Quote(name)) + "=";
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.String => left + Quote((string)v!),
            RegistryValueKind.DWord => left + $"dword:{(int)v!:x8}",
            RegistryValueKind.Binary => Hex(left + "hex:", (byte[])v!),
            RegistryValueKind.None => Hex(left + "hex(0):", v as byte[] ?? []),
            RegistryValueKind.QWord => Hex(left + "hex(b):", BitConverter.GetBytes((long)v!)),
            RegistryValueKind.ExpandString => Hex(left + "hex(2):", Encoding.Unicode.GetBytes((string)v! + "\0")),
            RegistryValueKind.MultiString => Hex(left + "hex(7):", Encoding.Unicode.GetBytes(string.Concat(((string[])v!).Select(s => s + "\0")) + "\0")),
            var other => throw new WlbError($@"{k.Name}\{name}: unsupported type {other}"),
        };
    }

    private static string Quote(string s) => "\"" + s.Replace(@"\", @"\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Writes hex as comma-separated; when a line is full, continues with a backslash.</summary>
    private static string Hex(string left, byte[] bytes)
    {
        var sb = new StringBuilder(left);
        var col = left.Length;
        for (var i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
            col += 2;
            if (i == bytes.Length - 1) break;
            sb.Append(',');
            if (++col < 77) continue;
            sb.Append("\\\r\n  ");
            col = 2;
        }
        return sb.ToString();
    }

    /// <summary>Writes .reg text into the registry. A missing key is created, a value is overwritten.</summary>
    public static void Import(string text)
    {
        RegistryKey? key = null;
        try
        {
            foreach (var raw in Regex.Replace(text, @"\\\r?\n\s*", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line == "" || line == Header) continue;
                if (line.StartsWith('['))
                {
                    if (!line.EndsWith(']') || !line.StartsWith("[" + Hklm, StringComparison.OrdinalIgnoreCase)) throw new WlbError($"Unsupported key: {line}");
                    key?.Dispose();
                    key = Registry.LocalMachine.CreateSubKey(line[(Hklm.Length + 1)..^1]);
                    continue;
                }
                var m = Regex.Match(line, @"^(@|""(?:[^""\\]|\\.)*"")=(.*)$");
                if (!m.Success || key is null) throw new WlbError($"Cannot parse this line: {line}");
                var name = m.Groups[1].Value == "@" ? "" : Unquote(m.Groups[1].Value);
                Set(key, name, m.Groups[2].Value);
            }
        }
        finally { key?.Dispose(); }
    }

    private static void Set(RegistryKey key, string name, string v)
    {
        if (v.StartsWith('"')) { key.SetValue(name, Unquote(v), RegistryValueKind.String); return; }
        if (v.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(name, unchecked((int)uint.Parse(v[6..], NumberStyles.HexNumber)), RegistryValueKind.DWord);
            return;
        }
        var m = Regex.Match(v, @"^hex(?:\((\w+)\))?:(.*)$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new WlbError($"Cannot parse this value: {name}={v}");
        var bytes = m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();
        var text = Encoding.Unicode.GetString(bytes);
        switch (m.Groups[1].Value.ToLowerInvariant())
        {
            case "": key.SetValue(name, bytes, RegistryValueKind.Binary); break;
            case "0": key.SetValue(name, bytes, RegistryValueKind.None); break;
            case "b": key.SetValue(name, BitConverter.ToInt64(bytes, 0), RegistryValueKind.QWord); break;
            case "2": key.SetValue(name, text.TrimEnd('\0'), RegistryValueKind.ExpandString); break;
            case "7": key.SetValue(name, text.TrimEnd('\0').Split('\0'), RegistryValueKind.MultiString); break;
            default: throw new WlbError($"Unsupported type: {name}={v}");
        }
    }

    private static string Unquote(string s) => Regex.Replace(s[1..^1], @"\\(.)", "$1");
}
