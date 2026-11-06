using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace WinLayerBoot.Core;

/// <summary>WIM image: a replacement for dism /Apply-Image, calls wimgapi.dll directly, progress goes through a callback.</summary>
public static class Wim
{
    private const uint GenericRead = 0x80000000, OpenExisting = 3, MsgProgress = 0x9478;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint MessageCallback(uint message, IntPtr wParam, IntPtr lParam, IntPtr user);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WIMCreateFile(string path, uint access, uint creation, uint flags, uint compression, out uint result);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WIMSetTemporaryPath(IntPtr wim, string path);

    [DllImport("wimgapi.dll", SetLastError = true)]
    private static extern int WIMGetImageCount(IntPtr wim);

    [DllImport("wimgapi.dll", SetLastError = true)]
    private static extern IntPtr WIMLoadImage(IntPtr wim, uint index);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WIMApplyImage(IntPtr image, string path, uint flags);

    [DllImport("wimgapi.dll", SetLastError = true)]
    private static extern uint WIMRegisterMessageCallback(IntPtr wim, MessageCallback callback, IntPtr user);

    [DllImport("wimgapi.dll", SetLastError = true)]
    private static extern bool WIMUnregisterMessageCallback(IntPtr wim, MessageCallback callback);

    [DllImport("wimgapi.dll", SetLastError = true)]
    private static extern bool WIMCloseHandle(IntPtr handle);

    private static WlbError Fail(string what) => new($"{what} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private static IntPtr Open(string wim)
    {
        var h = WIMCreateFile(wim, GenericRead, OpenExisting, 0, 0, out _);
        return h != IntPtr.Zero ? h : throw Fail($"Open {wim}");
    }

    public static int ImageCount(string wim)
    {
        var h = Open(wim);
        try { return WIMGetImageCount(h); }
        finally { WIMCloseHandle(h); }
    }

    /// <summary>Applies the image at index to dir (an existing directory); progress receives a 0-100 percentage (called back on the same thread).</summary>
    public static void Apply(string wim, int index, string dir, Action<int> progress)
    {
        Privileges.Enable("SeBackupPrivilege", "SeRestorePrivilege", "SeSecurityPrivilege", "SeTakeOwnershipPrivilege");
        var h = Open(wim);
        MessageCallback cb = (msg, w, _, _) =>
        {
            if (msg == MsgProgress) progress((int)w);
            return 0;
        };
        var image = IntPtr.Zero;
        try
        {
            if (!WIMSetTemporaryPath(h, Path.GetTempPath())) throw Fail("Set WIM temporary folder");
            if (WIMRegisterMessageCallback(h, cb, IntPtr.Zero) == uint.MaxValue) throw Fail("Register WIM progress callback");
            image = WIMLoadImage(h, (uint)index);
            if (image == IntPtr.Zero) throw Fail($"Load image {index}");
            if (!WIMApplyImage(image, dir, 0)) throw Fail("Unpack image");
        }
        finally
        {
            if (image != IntPtr.Zero) WIMCloseHandle(image);
            WIMUnregisterMessageCallback(h, cb);
            WIMCloseHandle(h);
            GC.KeepAlive(cb);
        }
    }
}
