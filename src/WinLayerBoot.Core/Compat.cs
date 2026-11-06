using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WinLayerBoot.Core;

/// <summary>Constructs that work on both net48 and net10: the core is compiled twice from the same source (see WinLayerBoot.Core.csproj).</summary>
public static class Compat
{
    public static int ProcessId { get; } = Process.GetCurrentProcess().Id;

    /// <summary>Move with overwrite.</summary>
    public static void MoveOver(string from, string to)
    {
        if (File.Exists(to)) File.Replace(from, to, null);
        else File.Move(from, to);
    }

    public static string Sha256Hex(Stream s)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
    }

    /// <summary>Reads until buf is full; throws if it cannot be filled (file too short).</summary>
    public static void ReadAll(Stream s, byte[] buf)
    {
        var n = 0;
        while (n < buf.Length)
        {
            var r = s.Read(buf, n, buf.Length - n);
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }
    }

    public static Guid GuidAt(byte[] b, int offset)
    {
        var g = new byte[16];
        Array.Copy(b, offset, g, 0, 16);
        return new Guid(g);
    }

    public static string NewTempDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Builds arguments by the Windows command-line rules (the reverse of CommandLineToArgvW): quote only if there are spaces or quotes; quotes and the backslashes before them are escaped.</summary>
    public static string JoinArgs(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    private static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

#if NETFRAMEWORK
    // Small methods that .NET Framework lacks but .NET has: on net10 the built-in ones are used

    public static TV? GetValueOrDefault<TK, TV>(this Dictionary<TK, TV> d, TK key) where TK : notnull => d.TryGetValue(key, out var v) ? v : default;
    public static TV GetValueOrDefault<TK, TV>(this Dictionary<TK, TV> d, TK key, TV def) where TK : notnull => d.TryGetValue(key, out var v) ? v : def;
    public static TV? GetValueOrDefault<TK, TV>(this IReadOnlyDictionary<TK, TV> d, TK key) => d.TryGetValue(key, out var v) ? v : default;
    public static TV GetValueOrDefault<TK, TV>(this IReadOnlyDictionary<TK, TV> d, TK key, TV def) => d.TryGetValue(key, out var v) ? v : def;
    public static bool TryAdd<TK, TV>(this Dictionary<TK, TV> d, TK key, TV value) where TK : notnull
    {
        if (d.ContainsKey(key)) return false;
        d.Add(key, value);
        return true;
    }
    public static void Deconstruct<TK, TV>(this KeyValuePair<TK, TV> p, out TK k, out TV v) { k = p.Key; v = p.Value; }
    public static string[] Split(this string s, char c, StringSplitOptions options) => s.Split([c], options);
    public static string[] Split(this string s, char c, int count) => s.Split([c], count);
    public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
    public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
    public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
    public static void Write(this Stream s, byte[] b) => s.Write(b, 0, b.Length);
#endif
}
