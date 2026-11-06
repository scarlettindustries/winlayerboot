using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinLayerBoot.Core;

/// <summary>Privileges in the process token: those an administrator has but that are not enabled by default (needed for loading hives and applying WIM); enabled once when first used, then effective for the whole process. Cannot be enabled when not an administrator; later calls report their own errors.</summary>
internal static class Privileges
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivilege
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeFileHandle token, bool disableAll, ref TokenPrivilege state, uint length, IntPtr previous, IntPtr returnLength);

    private static readonly ConcurrentDictionary<string, bool> _done = new();

    public static void Enable(params string[] names)
    {
        foreach (var n in names) _done.GetOrAdd(n, EnableOne);
    }

    private static bool EnableOne(string name)
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x28, out var token)) return false;   // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
        using (token)
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;
            var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };   // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
