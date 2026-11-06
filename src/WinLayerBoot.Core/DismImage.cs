using System;
using System.Runtime.InteropServices;

namespace WinLayerBoot.Core;

/// <summary>
/// DISM session (dismapi.dll) for an offline image: add drivers, enable features, disable features.
/// While a session is open DISM holds the hives in the image, so close it before loading a hive or unmounting the volume.
/// </summary>
public sealed class DismImage : IDisposable
{
    private const int LogErrorsWarningsInfo = 2;
    private delegate void ProgressCallback(uint current, uint total, nint userData);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)] private static extern int DismInitialize(int logLevel, string? logFile, string? scratchDir);
    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)] private static extern int DismOpenSession(string imagePath, string? windowsDir, string? systemDrive, out uint session);
    [DllImport("dismapi.dll")] private static extern int DismCloseSession(uint session);
    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)] private static extern int DismAddDriver(uint session, string inf, bool forceUnsigned);
    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)] private static extern int DismAddCapability(uint session, string name, bool limitAccess, string[] sources, uint sourceCount, nint cancel, ProgressCallback progress, nint userData);
    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)] private static extern int DismDisableFeature(uint session, string feature, string? package, bool removePayload, nint cancel, ProgressCallback progress, nint userData);
    [DllImport("dismapi.dll")] private static extern int DismGetLastErrorMessage(out nint message);
    [DllImport("dismapi.dll")] private static extern int DismDelete(nint p);

    // Initialize only once per process
    private static readonly Lazy<int> _init = new(() => DismInitialize(LogErrorsWarningsInfo, null, null));

    private readonly Output _out;
    private readonly uint _session;

    /// <param name="win">Folder the Windows partition is mounted to (no trailing backslash).</param>
    public DismImage(string win, Output output)
    {
        _out = output;
        Check("Initialize DISM", _init.Value);
        Check($"Open image {win}", DismOpenSession(win + @"\", null, null, out _session));
    }

    public void Dispose() => DismCloseSession(_session);

    public void AddDriver(string inf) => Check($"Inject {inf}", DismAddDriver(_session, inf, false));

    /// <summary>Adds features from the source directory, without accessing Windows Update.</summary>
    public void AddCapability(string name, string source)
    {
        Check($"Add {name}", DismAddCapability(_session, name, true, [source], 1, 0, Progress($"Add {name}"), 0));
        _out.Say($"    Added {name}");
    }

    public void DisableFeature(string name)
    {
        Check($"Disable {name}", DismDisableFeature(_session, name, null, false, 0, Progress($"Disable {name}"), 0));
        _out.Say($"    Disabled {name}");
    }

    private ProgressCallback Progress(string what) => (current, total, _) =>
    {
        if (total > 0) _out.Progress(what, (int)(current * 100UL / total));
    };

    private static void Check(string what, int hr)
    {
        if (hr >= 0) return;
        var why = "";
        if (DismGetLastErrorMessage(out var p) == 0 && p != 0)
        {
            why = ": " + Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
            DismDelete(p);
        }
        throw new WlbError($"{what} failed (0x{hr:X8}){why}");
    }
}
