using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace WinLayerBoot.Core;

/// <summary>Runs external programs (bcdboot).</summary>
public sealed class Proc(Output output)
{
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleOutputCP(uint cp);

    // While running, switch the console output code page to UTF-8 and restore it on exit; with no console (Web, WinUI), decode with the OEM code page
    private static Encoding _oem = Encoding.ASCII;
    private static uint _oldCp;
    private static bool _noWindow;

    public static void InitCodePage()
    {
#if !NETFRAMEWORK
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // .NET Framework ships with all code pages
#endif
        _oldCp = GetConsoleOutputCP();
        // With no console (WinUI freed the console that came with the double-click), child processes each create their own console window that flashes by: do not let them open windows
        _noWindow = _oldCp == 0;
        if (_oldCp != 0 && SetConsoleOutputCP(65001)) { _oem = new UTF8Encoding(false); return; }
        _oldCp = 0;
        try { _oem = Encoding.GetEncoding((int)GetOEMCP()); }
        catch { _oem = Encoding.ASCII; }
    }

    public static void RestoreCodePage()
    {
        if (_oldCp != 0) SetConsoleOutputCP(_oldCp);
    }

    private readonly Output _out = output;

    /// <summary>Runs and collects stdout and stderr (merged, in arrival order).</summary>
    public static (int Code, List<string> Lines) Capture(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe, Compat.JoinArgs(args))
        {
            UseShellExecute = false,
            CreateNoWindow = _noWindow,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = _oem,
            StandardErrorEncoding = _oem,
        };

        List<string> lines = [];
        var gate = new object();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) lines.Add(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return (p.ExitCode, lines);
    }

    /// <summary>Runs and checks the exit code; when a filter is given, prints only matching lines (case-insensitive).</summary>
    public void Exec(string what, string? filter, string exe, params string[] args)
    {
        var (code, lines) = Capture(exe, args);
        var re = filter is null ? null : new Regex(filter, RegexOptions.IgnoreCase);
        foreach (var l in lines)
        {
            if (re is null) _out.Say("    " + l);
            else if (re.IsMatch(l)) _out.Say("    " + l.Trim());
        }
        if (code == 0) return;
        // On failure, also print the filtered-out output, otherwise the cause is not visible
        if (re is not null)
            foreach (var l in lines.Where(l => !string.IsNullOrWhiteSpace(l) && !re.IsMatch(l))) _out.Say("    " + l.Trim(), Level.Dim);
        throw new WlbError($"{what} failed, exit code {code}{(code < 0 ? $" (0x{code:X8})" : "")}");
    }

}

public static class Files
{
    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); else if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    /// <summary>Removes inherited permissions and leaves only Administrators and SYSTEM with full control (what sshd requires of administrators_authorized_keys).</summary>
    public static void RestrictToAdmins(string path)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    /// <summary>Usable as a file name (without extension): contains none of \ / : * ? " &lt; &gt; |.</summary>
    public static bool IsValidName(string s) => Regex.IsMatch(s, @"^[^\\/:*?""<>|]+$");

    public static readonly Encoding Utf8Bom = new UTF8Encoding(true);
    public static readonly Encoding Utf16 = new UnicodeEncoding(false, true);

    /// <summary>Equivalent to Set-Content: appends a newline after the content.</summary>
    public static void WriteText(string path, string text, Encoding enc) => File.WriteAllText(path, text + "\r\n", enc);

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Compat.Sha256Hex(s);
    }

    public static string MB(double bytes) => Math.Round(bytes / (1 << 20)).ToString();
    public static string GB1(double bytes) => Math.Round(bytes / (1L << 30), 1).ToString("0.#");

    /// <summary>Free space (GB) of the drive the directory is on; 0 if the drive does not exist.</summary>
    public static long FreeGB(string dir)
    {
        try { return (long)Math.Round(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace / (double)(1L << 30)); }
        catch { return 0; }
    }

    public static bool IsAdmin() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

/// <summary>Small string helpers (C# 14 extension members).</summary>
public static class TextExt
{
    extension(string s)
    {
        /// <summary>N, no, none or empty: means "do nothing".</summary>
        public bool IsNo => Regex.IsMatch(s, "^(n|no|none|false|0|)$", RegexOptions.IgnoreCase);

        public bool IsYes => Regex.IsMatch(s, "^(y|yes|true|1)$", RegexOptions.IgnoreCase);

        /// <summary>Removes a trailing .vhdx.</summary>
        public string StripVhdx() => Regex.Replace(s, @"\.vhdx$", "", RegexOptions.IgnoreCase);

        /// <summary>PowerShell -like: * and ? wildcards, case-insensitive.</summary>
        public bool Like(string pattern) =>
            Regex.IsMatch(s, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

        public bool EqualsIgnoreCase(string? other) => string.Equals(s, other, StringComparison.OrdinalIgnoreCase);
    }
}
