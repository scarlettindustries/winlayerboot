using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace WinLayerBoot.Core;

/// <summary>
/// Reads and writes through the BCD WMI provider (root\wmi): elements are fetched by type number, bcdedit output is not parsed, and the system language does not matter.
/// Values are all converted to strings for easy comparison: lists are joined with newlines, booleans are True / False, and devices are written the way bcdedit shows them (vhd=[V:]\x.vhdx, partition=C:).
/// When file is empty, it is the system BCD.
/// </summary>
public sealed class BcdStore(string file = "")
{
    public const uint AllObjects = 0, OsLoader = 0x10200003;
    public const uint Device = 0x11000001, OsDevice = 0x21000001, Description = 0x12000004, Locale = 0x12000005,
        BootEms = 0x16000020, BootShutdownDisabled = 0x16000074,
        DefaultObject = 0x23000003, ResumeObject = 0x23000003, BootMgrResume = 0x23000006,
        DisplayOrder = 0x24000001, BootSequence = 0x24000002, Timeout = 0x25000004, Nx = 0x25000020, BootMenuPolicy = 0x250000c2,
        DisplayBootMenu = 0x26000020, WinPE = 0x26000022, Ems = 0x260000b0;
    public const uint EmsPort = 0x15000022, EmsBaudRate = 0x15000023;
    public const string CurrentId = "{fa926493-6f1c-4193-a414-58f0b2456d1e}", EmsSettingsId = "{0ce4991b-e6b3-4b16-b23c-5e0d9250e5d9}";

    private static readonly ManagementScope Scope = new(@"root\wmi", new ConnectionOptions { EnablePrivileges = true });
    private readonly string _file = file.Replace(@"\", @"\\");

    /// <summary>The name shown for an element in messages (same as bcdedit); unknown ones show the type number.</summary>
    public static string ElementName(uint type) => type switch
    {
        Device => "device", OsDevice => "osdevice", Description => "description", Locale => "locale",
        BootEms => "bootems", BootShutdownDisabled => "bootshutdowndisabled", ResumeObject => "resumeobject/default", BootMgrResume => "resumeobject",
        DisplayOrder => "displayorder", BootSequence => "bootsequence", Timeout => "timeout", Nx => "nx", BootMenuPolicy => "bootmenupolicy",
        DisplayBootMenu => "displaybootmenu", WinPE => "winpe", Ems => "ems",
        _ => $"0x{type:x8}",
    };

    public List<string> Objects(uint type)
    {
        using var store = Store();
        using var r = Call(store, "EnumerateObjects", ("Type", type));
        return ((ManagementBaseObject[]?)r["Objects"] ?? []).Select(o => (string)o["Id"]).ToList();
    }

    /// <summary>All elements of an object: type number -> value.</summary>
    public Dictionary<uint, string> Elements(string id)
    {
        using var o = Object(id);
        using var r = Call(o, "EnumerateElements");
        return ((ManagementBaseObject[]?)r["Elements"] ?? []).ToDictionary(e => (uint)e["Type"], Text);
    }

    /// <summary>Writes one element; which kind of write is decided by the type number (bits 24 to 27 are the format).</summary>
    public void Set(string id, uint type, string value)
    {
        using var o = Object(id);
        using var r = ((type >> 24) & 0xF) switch
        {
            2 => Call(o, "SetStringElement", ("Type", type), ("String", value)),
            3 => Call(o, "SetObjectElement", ("Type", type), ("Id", value)),
            4 => Call(o, "SetObjectListElement", ("Type", type), ("Ids", value.Split('\n', StringSplitOptions.RemoveEmptyEntries))),
            5 => Call(o, "SetIntegerElement", ("Type", type), ("Integer", ulong.Parse(value))),
            6 => Call(o, "SetBooleanElement", ("Type", type), ("Boolean", bool.Parse(value))),
            _ => throw new WlbError($"Writing BCD element is not supported: {ElementName(type)}"),
        };
    }

    public void Delete(string id, uint type)
    {
        using var o = Object(id);
        using var r = Call(o, "DeleteElement", ("Type", type));
    }

    /// <summary>Both device and osdevice point to this VHDX (same as what bcdedit /set device vhd=[V:]\x.vhdx writes).</summary>
    public void SetVhd(string id, string vhdx)
    {
        using var o = Object(id);
        foreach (var (type, locate) in new[] { (Device, 0x12000002u), (OsDevice, 0x22000002u) })
        {
            using var r = Call(o, "SetVhdDeviceElement", ("Type", type), ("CustomLocate", locate), ("ParentDeviceType", 2u),
                ("ParentAdditionalOptions", ""), ("ParentPath", DosDevice(vhdx[..2])), ("Path", vhdx[2..]));
        }
    }

    /// <summary>Copies an object from the system store into this store and returns the new Id. A store file cannot be its own source. The new object is not in the boot menu.</summary>
    public string Copy(string fromId)
    {
        using var store = Store();
        using var r = Call(store, "CopyObject", ("Flags", 1u), ("SourceId", fromId), ("SourceStoreFile", ""));
        return (string)((ManagementBaseObject)r["Object"])["Id"];
    }

    /// <summary>Deletes an object. Only the object itself is deleted; references in the menu order must be removed by the caller.</summary>
    public void DeleteObject(string id)
    {
        using var store = Store();
        using var r = Call(store, "DeleteObject", ("Id", id));
    }

    /// <summary>The actual Id of the current system boot entry ({current} is an alias, and the default entry cannot be written as an alias).</summary>
    public string Current()
    {
        using var store = Store();
        using var r = Call(store, "OpenObject", ("Id", CurrentId));
        return (string)((ManagementBaseObject)r["Object"])["Id"];
    }

    /// <summary>Exports the whole store to a file.</summary>
    public void Export(string path)
    {
        using var store = Store();
        using var r = Call(store, "ExportStore", ("File", path));
    }

    private ManagementObject Store() => new(Scope, new ManagementPath($"BcdStore.FilePath=\"{_file}\""), null);
    private ManagementObject Object(string id) => new(Scope, new ManagementPath($"BcdObject.Id=\"{id}\",StoreFilePath=\"{_file}\""), null);

    private static ManagementBaseObject Call(ManagementObject o, string method, params (string Name, object Value)[] args)
    {
        using var input = args.Length > 0 ? o.GetMethodParameters(method) : null;
        foreach (var (name, value) in args) input![name] = value;
        var r = o.InvokeMethod(method, input, null);
        if (r["ReturnValue"] is false) { r.Dispose(); throw new WlbError($"BCD {method} failed ({o.Path.RelativePath})"); }
        return r;
    }

    private static string Text(ManagementBaseObject e) => e.ClassPath.ClassName switch
    {
        "BcdStringElement" => (string)e["String"],
        "BcdObjectElement" => (string)e["Id"],
        "BcdObjectListElement" => string.Join("\n", (string[]?)e["Ids"] ?? []),
        "BcdIntegerElement" => Convert.ToString(e["Integer"])!,
        "BcdIntegerListElement" => string.Join("\n", ((Array?)e["Integers"] ?? Array.Empty<object>()).Cast<object>()),
        "BcdBooleanElement" => Convert.ToString(e["Boolean"])!,
        "BcdDeviceElement" => DeviceText((ManagementBaseObject)e["Device"]),
        var other => other,
    };

    private static string DeviceText(ManagementBaseObject d)
    {
        var parent = Prop(d, "Parent") as ManagementBaseObject;
        return Convert.ToUInt32(d["DeviceType"]) switch
        {
            2 => $"partition={Letter(Prop(d, "Path") as string ?? "")}",
            3 => $"file={FilePath(d)}",
            4 => $"ramdisk={FilePath(d)},{Prop(d, "AdditionalOptions")}",
            7 or 8 when parent is not null => $"vhd={FilePath(parent)}",   // Booting from VHD: the locate device wraps a file device
            var t => $"device type {t}",
        };
    }

    private static string FilePath(ManagementBaseObject d) =>
        $"[{Letter(Prop(Prop(d, "Parent") as ManagementBaseObject, "Path") as string ?? "")}]{Prop(d, "Path")}";

    /// <summary>The fields of device data differ by type; fields that are absent come out as null.</summary>
    private static object? Prop(ManagementBaseObject? o, string name) =>
        o?.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name == name)?.Value;

    /// <summary>Replaces \Device\HarddiskVolumeN with a drive letter; returns it unchanged if there is no drive letter.</summary>
    private static string Letter(string device) =>
        DriveInfo.GetDrives().Select(d => d.Name[..2]).FirstOrDefault(l => DosDevice(l).EqualsIgnoreCase(device)) ?? device;

    private static string DosDevice(string letter)
    {
        var buf = new char[512];
        var n = QueryDosDeviceW(letter, buf, (uint)buf.Length);
        return n == 0 ? "" : new string(buf).Split('\0')[0];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string name, [Out] char[] target, uint max);
}
