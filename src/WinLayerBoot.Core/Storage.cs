using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WinLayerBoot.Core;

/// <summary>
/// Storage: replacement for Get-DiskImage, Mount-DiskImage, Get-Partition and Get-Volume, calling Win32 APIs directly.
/// VHDX and ISO are both mounted without a drive letter (NO_DRIVE_LETTER); volumes for external tools are mounted at a folder (volume mount point).
/// Waiting for a volume to appear uses Plug and Play notifications (CM_Register_Notification), not a fixed-length wait.
/// </summary>
public static class Storage
{
    public sealed class Img
    {
        public bool Attached;
        public uint? Number;
        public ulong Size;
    }

    public sealed class Part
    {
        public uint Number, Disk;
        public ulong Offset, Size;
        public char Letter;
        public string Type = "";
        public string VolumeName = "";   // \\?\Volume{...}\, empty when there is no volume
    }

    public sealed class Vol
    {
        public char Letter;
        public string FileSystem = "", Label = "", UniqueId = "";
        public ulong Size, SizeRemaining;
    }

    // ---------------------------------------------------------------- virtdisk.dll

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref VirtualStorageType type, string path, uint access, uint flags, IntPtr parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint size, IntPtr info, IntPtr sizeUsed);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint sizeInBytes, [Out] char[] path);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle handle, IntPtr securityDescriptor, uint flags, uint providerFlags, IntPtr parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint CreateVirtualDisk(ref VirtualStorageType type, string path, uint access, IntPtr securityDescriptor, uint flags, uint providerFlags, IntPtr parameters, IntPtr overlapped, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint ResizeVirtualDisk(SafeFileHandle handle, uint flags, IntPtr parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskOperationProgress(SafeFileHandle handle, IntPtr overlapped, IntPtr progress);

    // VIRTUAL_STORAGE_TYPE_DEVICE_*: 1 ISO, 2 VHD, 3 VHDX
    private static uint DeviceId(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".iso" => 1u,
        ".vhd" => 2u,
        _ => 3u,
    };

    private const uint AccessNone = 0, AccessDetach = 0x00040000, AccessRead = 0x000D0000;   // READ = ATTACH_RO | DETACH | GET_INFO
    private const uint AttachReadOnly = 1, AttachNoDriveLetter = 2, AttachPermanent = 4;

    /// <summary>
    /// Opens a virtual disk. ISO uses default parameters and read-only access; VHD/VHDX use OPEN_VIRTUAL_DISK_VERSION_2.
    /// Always set readOnly as needed: when not set, the underlying file is opened writable, and a VHDX with an unreplayed log (a child disk that has been booted natively) would be replayed and rewritten.
    /// </summary>
    private static SafeFileHandle? Open(string path, bool getInfoOnly = true, bool readOnly = true)
    {
        var type = new VirtualStorageType { DeviceId = DeviceId(path), VendorId = VendorMicrosoft };
        var full = Path.GetFullPath(path);
        if (type.DeviceId == 1)
            return OpenVirtualDisk(ref type, full, AccessRead, 0, IntPtr.Zero, out var hi) == 0 ? hi : null;
        var p = Marshal.AllocHGlobal(64);
        try
        {
            for (var i = 0; i < 64; i += 4) Marshal.WriteInt32(p, i, 0);
            Marshal.WriteInt32(p, 0, 2);                       // Version = OPEN_VIRTUAL_DISK_VERSION_2
            Marshal.WriteInt32(p, 4, getInfoOnly ? 1 : 0);     // GetInfoOnly
            Marshal.WriteInt32(p, 8, readOnly ? 1 : 0);        // ReadOnly
            return OpenVirtualDisk(ref type, full, AccessNone, 0, p, out var h) == 0 ? h : null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Returns the physical path (\\.\PhysicalDriveN or \\.\CDROMN) when attached, null when not attached.</summary>
    private static string? PhysicalPath(SafeFileHandle h)
    {
        var buf = new char[512];
        var size = (uint)(buf.Length * 2);
        return GetVirtualDiskPhysicalPath(h, ref size, buf) == 0 ? new string(buf).TrimEnd('\0') : null;
    }

    private static ulong VirtualSize(SafeFileHandle h)
    {
        var p = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt64(p, 0, 0);
            Marshal.WriteInt32(p, 0, 1);   // GET_VIRTUAL_DISK_INFO_SIZE
            uint size = 1024;
            return GetVirtualDiskInformation(h, ref size, p, IntPtr.Zero) == 0 ? (ulong)Marshal.ReadInt64(p, 8) : 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Get-DiskImage: returns null if the file does not exist or cannot be opened.</summary>
    public static Img? DiskImage(string path)
    {
        if (!File.Exists(path)) return null;
        using var h = Open(path);
        if (h is null) return null;
        var phys = PhysicalPath(h);
        uint? n = null;
        if (phys is not null && phys.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) && uint.TryParse(phys.Substring(17), out var d)) n = d;
        return new Img { Attached = phys is not null, Number = n, Size = VirtualSize(h) };
    }

    public static bool IsAttached(string path) => DiskImage(path)?.Attached == true;

    /// <summary>Mounts a VHD/VHDX/ISO: no drive letter, stays mounted after the process exits (must be detached explicitly).</summary>
    public static void Attach(string path, bool readOnly)
    {
        var iso = DeviceId(path) == 1;
        using var h = (iso ? Open(path) : Open(path, getInfoOnly: false, readOnly: readOnly)) ?? throw new WlbError($"Cannot open {path}");
        var p = Marshal.AllocHGlobal(24);
        try
        {
            for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(p, i, 0);
            Marshal.WriteInt32(p, 0, 1);   // ATTACH_VIRTUAL_DISK_VERSION_1
            var flags = AttachNoDriveLetter | AttachPermanent | (iso || readOnly ? AttachReadOnly : 0);
            var r = AttachVirtualDisk(h, IntPtr.Zero, flags, 0, p, IntPtr.Zero);
            if (r != 0) throw new WlbError($"Failed to mount {path}: {new Win32Exception((int)r).Message}");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// Creates a VHD/VHDX (CREATE_VIRTUAL_DISK_VERSION_2, 64-bit layout): dynamically expanding, default block size and sector size;
    /// with parent given it is a differencing disk, and the size follows the parent (MaximumSize is 0).
    /// </summary>
    private static void Create(string path, ulong maxBytes, string? parent)
    {
        var type = new VirtualStorageType { DeviceId = DeviceId(path), VendorId = VendorMicrosoft };
        var parentPath = parent is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(Path.GetFullPath(parent));
        var p = Marshal.AllocHGlobal(128);
        try
        {
            for (var i = 0; i < 128; i += 8) Marshal.WriteInt64(p, i, 0);
            Marshal.WriteInt32(p, 0, 2);               // Version
            Marshal.WriteInt64(p, 24, (long)maxBytes); // MaximumSize
            Marshal.WriteIntPtr(p, 48, parentPath);    // ParentPath
            var r = CreateVirtualDisk(ref type, Path.GetFullPath(path), AccessNone, IntPtr.Zero, 0, 0, p, IntPtr.Zero, out var h);
            using (h)
                if (r != 0) throw new WlbError($"Failed to create {path}: {new Win32Exception((int)r).Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(parentPath);
        }
    }

    /// <summary>Creates a dynamically expanding VHD/VHDX.</summary>
    public static void CreateVhdx(string path, ulong maxBytes) => Create(path, maxBytes, null);

    /// <summary>Creates a differencing disk: path has parent as its parent disk.</summary>
    public static void CreateDifferencing(string path, string parent) => Create(path, 0, parent);

    /// <summary>
    /// Merges an existing disk (may be a differencing disk) into a standalone dynamically expanding disk, runs asynchronously and polls progress; progress receives a 0-100 percentage.
    /// The parameter block is CREATE_VIRTUAL_DISK_VERSION_2, 64-bit layout; MaximumSize 0 means the same size as the source.
    /// </summary>
    public static void CreateFrom(string path, string source, Action<int> progress)
    {
        const uint pending = 997;   // ERROR_IO_PENDING
        var type = new VirtualStorageType { DeviceId = DeviceId(path), VendorId = VendorMicrosoft };
        var sourcePath = Marshal.StringToHGlobalUni(Path.GetFullPath(source));
        var p = Marshal.AllocHGlobal(128);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var state = Marshal.AllocHGlobal(24);
        using var done = new ManualResetEvent(false);
        try
        {
            for (var i = 0; i < 128; i += 8) Marshal.WriteInt64(p, i, 0);
            Marshal.WriteInt32(p, 0, 2);               // Version
            Marshal.WriteIntPtr(p, 56, sourcePath);    // SourcePath
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            var r = CreateVirtualDisk(ref type, Path.GetFullPath(path), AccessNone, IntPtr.Zero, 0, 0, p, overlapped, out var h);
            using (h)
            {
                if (r != 0 && r != pending) throw new WlbError($"Failed to merge {source} into {path}: {new Win32Exception((int)r).Message}");
                var last = -1;
                while (true)
                {
                    var e = GetVirtualDiskOperationProgress(h, overlapped, state);
                    if (e != 0) throw new WlbError($"Failed to read merge progress: {new Win32Exception((int)e).Message}");
                    var status = (uint)Marshal.ReadInt32(state, 0);   // VIRTUAL_DISK_PROGRESS: OperationStatus, CurrentValue, CompletionValue
                    var total = Marshal.ReadInt64(state, 16);
                    var pct = status == 0 ? 100 : total > 0 ? (int)(Marshal.ReadInt64(state, 8) * 100 / total) : 0;
                    if (pct != last) progress(last = pct);
                    if (status == 0) return;
                    if (status != pending) throw new WlbError($"Failed to merge {source} into {path}: {new Win32Exception((int)status).Message}");
                    done.WaitOne(200);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(state);
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(sourcePath);
        }
    }

    /// <summary>Expands a VHD/VHDX that is not attached (RESIZE_VIRTUAL_DISK_VERSION_1). The partition must be expanded separately.</summary>
    public static void Resize(string path, ulong newBytes)
    {
        using var h = Open(path, getInfoOnly: false, readOnly: false) ?? throw new WlbError($"Cannot open {path}");
        var p = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(p, 0, 1);                // Version
            Marshal.WriteInt64(p, 8, (long)newBytes);   // NewSize
            var r = ResizeVirtualDisk(h, 0, p, IntPtr.Zero);
            if (r != 0) throw new WlbError($"Failed to expand {path}: {new Win32Exception((int)r).Message}");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Detaches a VHD/VHDX/ISO. Does nothing if it is not attached.</summary>
    public static void Detach(string path)
    {
        if (!IsAttached(path)) return;
        uint r;
        if (DeviceId(path) == 1)
        {
            using var hi = Open(path) ?? throw new WlbError($"Cannot open {path}");
            r = DetachVirtualDisk(hi, 0, 0);
        }
        else
        {
            // Reopening an attached VHDX only to detach it: first try the legacy open that needs only the DETACH permission, and if that fails use read-only VERSION_2
            var type = new VirtualStorageType { DeviceId = DeviceId(path), VendorId = VendorMicrosoft };
            r = OpenVirtualDisk(ref type, Path.GetFullPath(path), AccessDetach, 0, IntPtr.Zero, out var h1);
            if (r == 0) using (h1) r = DetachVirtualDisk(h1, 0, 0);
            if (r != 0)
            {
                using var h2 = Open(path, getInfoOnly: false, readOnly: true);
                if (h2 is not null) r = DetachVirtualDisk(h2, 0, 0);
            }
        }
        if (r != 0) throw new WlbError($"Failed to unmount {path}: {new Win32Exception((int)r).Message}");
    }

    // ---------------------------------------------------------------- Volume arrival notification

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CmNotifyCallback(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(IntPtr filter, IntPtr context, CmNotifyCallback callback, out IntPtr notify);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(IntPtr notify);

    private static readonly Guid VolumeInterface = new("53F5630D-B6BF-11D0-94F2-00A0C91EFB8B");   // GUID_DEVINTERFACE_VOLUME
    private static readonly Guid DiskInterface = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");     // GUID_DEVINTERFACE_DISK

    /// <summary>
    /// Volume arrival notification: create the watcher first, then mount, then wait with Until for the condition to hold.
    /// Registration does not report devices that already exist, so the order must be "register → trigger → check → wait for notification → check again".
    /// When the volume device has arrived but the file system is not mounted yet there is no separate event, so the wait wakes up at most once a second to check again.
    /// </summary>
    public sealed class VolumeWatcher : IDisposable
    {
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CmNotifyCallback _callback;   // Must be kept referenced so it is not garbage collected
        private readonly IntPtr _handle;

        public VolumeWatcher() : this(false) { }

        /// <summary>disk = true: wait for the disk device to arrive (a freshly attached blank VHDX has no volume, so only the disk can be waited for).</summary>
        public VolumeWatcher(bool disk)
        {
            _callback = (_, _, action, _, _) =>
            {
                if (action == 0) _signal.Release();   // CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL
                return 0;
            };
            // CM_NOTIFY_FILTER: cbSize, Flags, FilterType (0 = DEVICEINTERFACE), Reserved, with ClassGuid in the union; 416 bytes in total
            var f = Marshal.AllocHGlobal(416);
            try
            {
                for (var i = 0; i < 416; i += 8) Marshal.WriteInt64(f, i, 0);
                Marshal.WriteInt32(f, 0, 416);
                Marshal.Copy((disk ? DiskInterface : VolumeInterface).ToByteArray(), 0, f + 16, 16);
                var r = CM_Register_Notification(f, IntPtr.Zero, _callback, out _handle);
                if (r != 0) throw new WlbError($"Failed to register for volume arrival notifications: CONFIGRET {r}");
            }
            finally { Marshal.FreeHGlobal(f); }
        }

        /// <summary>Waits until ready() is true and returns how long it waited; throws on timeout.</summary>
        public TimeSpan Until(Func<bool> ready, TimeSpan timeout, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!ready())
            {
                var left = timeout - sw.Elapsed;
                if (left <= TimeSpan.Zero) throw new WlbError($"Timed out waiting for {what} ({timeout.TotalSeconds:0} s)");
                _signal.Wait(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            }
            return sw.Elapsed;
        }

        public void Dispose()
        {
            CM_Unregister_Notification(_handle);
            _signal.Dispose();
            GC.KeepAlive(_callback);
        }
    }

    // ---------------------------------------------------------------- Disks and volumes

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolume(StringBuilder name, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolume(IntPtr find, StringBuilder name, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool FindVolumeClose(IntPtr find);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] paths, uint length, out uint returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, StringBuilder label, uint labelLen, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, uint fsLen);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string dir, out ulong available, out ulong total, out ulong free);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetVolumeMountPoint(string mountPoint, string volume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteVolumeMountPoint(string mountPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volume, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string name, StringBuilder buffer, int length);

    private const uint GenericRead = 0x80000000, ShareRW = 3, OpenExisting = 3;
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050, IoctlVolumeGetDiskExtents = 0x00560000;

    private static string GptName(Guid g) => g.ToString() switch
    {
        "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "System",
        "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Reserved",
        "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic",
        "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Recovery",
        _ => "Unknown",
    };

    private static List<string> Volumes()
    {
        List<string> list = [];
        var sb = new StringBuilder(1024);
        var find = FindFirstVolume(sb, (uint)sb.Capacity);
        if (find == new IntPtr(-1)) return list;
        try
        {
            do list.Add(sb.ToString());
            while (FindNextVolume(find, sb, (uint)sb.Capacity));
        }
        finally { FindVolumeClose(find); }
        return list;
    }

    private static List<(string Name, uint Disk, ulong Offset)> VolumeExtents()
    {
        List<(string, uint, ulong)> list = [];
        var buf = Marshal.AllocHGlobal(4096);
        try
        {
            foreach (var name in Volumes())
            {
                using var h = CreateFile(name.TrimEnd('\\'), 0, ShareRW, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                if (!DeviceIoControl(h, IoctlVolumeGetDiskExtents, IntPtr.Zero, 0, buf, 4096, out _, IntPtr.Zero)) continue;
                if (Marshal.ReadInt32(buf, 0) != 1) continue;
                list.Add((name, (uint)Marshal.ReadInt32(buf, 8), (ulong)Marshal.ReadInt64(buf, 16)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private static List<string> PathsOf(string volumeName)
    {
        var buf = new char[4096];
        if (!GetVolumePathNamesForVolumeName(volumeName, buf, (uint)buf.Length, out _)) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static char LetterOf(string volumeName) =>
        PathsOf(volumeName).Where(p => p.Length == 3 && p[1] == ':').Select(p => char.ToUpperInvariant(p[0])).FirstOrDefault();

    /// <summary>Get-Partition -DiskNumber, sorted by Offset, with each partition's volume name and drive letter.</summary>
    public static List<Part> Partitions(uint disk)
    {
        List<Part> list = [];
        using var h = CreateFile($@"\\.\PhysicalDrive{disk}", GenericRead, ShareRW, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return list;
        const int size = 64 * 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(h, IoctlDiskGetDriveLayoutEx, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero)) return list;
            var gpt = Marshal.ReadInt32(buf, 0) == 1;
            var count = Marshal.ReadInt32(buf, 4);
            for (var i = 0; i < count; i++)
            {
                var e = buf + 48 + i * 144;
                var number = (uint)Marshal.ReadInt32(e, 24);
                if (number == 0) continue;
                var typeBytes = new byte[16];
                Marshal.Copy(e + 32, typeBytes, 0, 16);
                list.Add(new Part
                {
                    Disk = disk,
                    Number = number,
                    Offset = (ulong)Marshal.ReadInt64(e, 8),
                    Size = (ulong)Marshal.ReadInt64(e, 16),
                    Type = gpt ? GptName(new Guid(typeBytes)) : "Unknown",
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        var vols = VolumeExtents();
        foreach (var p in list)
        {
            var v = vols.FirstOrDefault(x => x.Disk == disk && x.Offset == p.Offset);
            if (v.Name is null) continue;
            p.VolumeName = v.Name;
            p.Letter = LetterOf(v.Name);
        }
        return list.OrderBy(p => p.Offset).ToList();
    }

    /// <summary>The disk device can be opened (after attach the kernel has already created the device, so diskpart can select disk).</summary>
    public static bool DiskReady(uint disk)
    {
        using var h = CreateFile($@"\\.\PhysicalDrive{disk}", GenericRead, ShareRW, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return !h.IsInvalid;
    }

    /// <summary>The disk number of an attached VHDX; throws if not attached.</summary>
    public static uint DiskNumber(string vhdx) =>
        DiskImage(vhdx) is { Attached: true, Number: uint n } ? n : throw new WlbError($"{vhdx} is not mounted");

    /// <summary>The partitions of an attached VHDX; returns empty if not attached. To wait for a volume to appear, use VolumeWatcher.</summary>
    public static List<Part> PartitionsOf(string vhdx) =>
        DiskImage(vhdx) is { Attached: true, Number: uint n } ? Partitions(n) : [];

    /// <summary>Removes the drive letter from a partition's volume (the system may have assigned one automatically when the partition was created).</summary>
    public static void RemoveLetter(Part p)
    {
        if (p.Letter == '\0') return;
        if (!DeleteVolumeMountPoint($@"{p.Letter}:\"))
            throw new WlbError($"Failed to remove drive letter {p.Letter}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        p.Letter = '\0';
    }

    private static Vol ToVol(string volumeName)
    {
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        var v = new Vol { UniqueId = volumeName, Letter = LetterOf(volumeName) };
        if (GetVolumeInformation(volumeName, label, 261, out _, out _, out _, fs, 261))
        {
            v.Label = label.ToString();
            v.FileSystem = fs.ToString();
        }
        if (GetDiskFreeSpaceEx(volumeName, out _, out var total, out var freeBytes))
        {
            v.Size = total;
            v.SizeRemaining = freeBytes;
        }
        return v;
    }

    public static Vol? VolumeOf(Part p) => p.VolumeName == "" ? null : ToVol(p.VolumeName);

    /// <summary>The ISO's volume: the device for the physical path \\.\CDROMn and the volume's device are the same.</summary>
    public static Vol? VolumeOfImage(string path)
    {
        if (!File.Exists(path)) return null;
        string? phys;
        using (var h = Open(path))
        {
            if (h is null) return null;
            phys = PhysicalPath(h);
        }
        if (phys is null) return null;
        var dev = new StringBuilder(1024);
        if (QueryDosDevice(phys.Replace(@"\\.\", ""), dev, dev.Capacity) == 0) return null;
        var want = dev.ToString().Split('\0')[0];
        foreach (var name in Volumes())
        {
            var d = new StringBuilder(1024);
            if (QueryDosDevice(name.Substring(4).TrimEnd('\\'), d, d.Capacity) != 0 &&
                string.Equals(d.ToString().Split('\0')[0], want, StringComparison.OrdinalIgnoreCase))
                return ToVol(name);
        }
        return null;
    }

    // ---------------------------------------------------------------- Folder mount points

    public static void MountAt(string dir, string volumeName)
    {
        Directory.CreateDirectory(dir);
        if (Directory.EnumerateFileSystemEntries(dir).Any()) throw new WlbError($"{dir} is not an empty folder and cannot be used as a mount point");
        if (!SetVolumeMountPoint(dir.TrimEnd('\\') + @"\", volumeName))
            throw new WlbError($"Failed to mount to {dir}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public static string? VolumeAt(string dir)
    {
        var sb = new StringBuilder(260);
        return Directory.Exists(dir) && GetVolumeNameForVolumeMountPoint(dir.TrimEnd('\\') + @"\", sb, 260) ? sb.ToString() : null;
    }

    public static bool IsMountPoint(string dir) => VolumeAt(dir) is not null;

    /// <summary>Removes the mount point and deletes the empty folder. A folder with a volume mounted must never be deleted recursively, so remove the mount first and then delete the directory (non-recursive).</summary>
    public static void UnmountAt(string dir)
    {
        if (IsMountPoint(dir) && !DeleteVolumeMountPoint(dir.TrimEnd('\\') + @"\"))
            throw new WlbError($"Failed to remove mount point {dir}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
