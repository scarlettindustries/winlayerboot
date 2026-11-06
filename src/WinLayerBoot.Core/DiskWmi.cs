using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;

namespace WinLayerBoot.Core;

/// <summary>Partition operations: via storage WMI (root\Microsoft\Windows\Storage). New partitions get no drive letter.</summary>
public static class DiskWmi
{
    public const string EspType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", MsrType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}",
        BasicType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}", RecoveryType = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";

    private static readonly ManagementScope Scope = new(@"root\Microsoft\Windows\Storage");
    private static readonly TimeSpan AppearTimeout = TimeSpan.FromSeconds(15);   // A just-created disk, partition or volume takes a while to become visible in WMI

    /// <summary>Initializes a new disk as GPT.</summary>
    public static void InitializeGpt(uint disk)
    {
        using var d = Disk(disk);
        using var r = Call(d, "Initialize", ("PartitionStyle", (ushort)2));
    }

    /// <summary>Creates a partition and returns the partition number. size 0 means use the remaining space, offset 0 means place it right after the previous one.</summary>
    public static uint CreatePartition(uint disk, string gptType, ulong size = 0, ulong offset = 0)
    {
        List<(string, object)> args = [("GptType", gptType), size == 0 ? ("UseMaximumSize", true) : ("Size", size)];
        if (offset != 0) args.Add(("Offset", offset));
        using var d = Disk(disk);
        using var r = Call(d, "CreatePartition", [.. args]);
        return Convert.ToUInt32(((ManagementBaseObject)r["CreatedPartition"])["PartitionNumber"]);
    }

    /// <summary>Quick format.</summary>
    public static void Format(uint disk, uint partition, string fileSystem, string label)
    {
        using var p = Partition(disk, partition);
        using var v = Wait($"volume of disk {disk} partition {partition}", () => p.GetRelated("MSFT_Volume").Cast<ManagementObject>().FirstOrDefault());
        using var r = Call(v, "Format", ("FileSystem", fileSystem), ("FileSystemLabel", label), ("Full", false), ("Force", true));
    }

    public static void DeletePartition(uint disk, uint partition)
    {
        using var p = Partition(disk, partition);
        using var r = Call(p, "DeleteObject");
    }

    /// <summary>Extends the partition until the free space after it is used up.</summary>
    public static void Extend(uint disk, uint partition)
    {
        using var p = Partition(disk, partition);
        using var s = Call(p, "GetSupportedSize");
        using var r = Call(p, "Resize", ("Size", Convert.ToUInt64(s["SizeMax"])));
    }

    private static ManagementObject Disk(uint disk) =>
        Wait($"disk {disk}", () => First($"SELECT * FROM MSFT_Disk WHERE Number={disk}"));

    private static ManagementObject Partition(uint disk, uint partition) =>
        Wait($"disk {disk} partition {partition}", () => First($"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk} AND PartitionNumber={partition}"));

    private static ManagementObject? First(string query)
    {
        using var s = new ManagementObjectSearcher(Scope, new ObjectQuery(query));
        return s.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static ManagementObject Wait(string what, Func<ManagementObject?> find)
    {
        var t = Stopwatch.StartNew();
        while (true)
        {
            if (find() is { } o) return o;
            if (t.Elapsed > AppearTimeout) throw new WlbError($"Waited {AppearTimeout.TotalSeconds:0} s, but WMI still has no {what}");
            Thread.Sleep(200);
        }
    }

    private static ManagementBaseObject Call(ManagementObject o, string method, params (string Name, object Value)[] args)
    {
        using var input = args.Length > 0 ? o.GetMethodParameters(method) : null;
        foreach (var (name, value) in args) input![name] = value;
        var r = o.InvokeMethod(method, input, null);
        var code = Convert.ToUInt32(r["ReturnValue"]);
        if (code != 0) { r.Dispose(); throw new WlbError($"Storage WMI {method} failed, returned {code} ({o.ClassPath.ClassName})"); }
        return r;
    }
}
