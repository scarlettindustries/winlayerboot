using System.Collections.Generic;
using System.Linq;

namespace WinLayerBoot.Core.Api;

// Data structures shared by the web API and the frontends (JSON source generation: see WlbJson)

public sealed class FieldInfo
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Default { get; set; } = "";
    public string Help { get; set; } = "";
    public string Group { get; set; } = "";
    public List<string> Choices { get; set; } = [];
    public bool Strict { get; set; }
    public bool Advanced { get; set; }

    public static FieldInfo From(Field f) => new()
    {
        Key = f.Key, Label = f.Label, Kind = f.Kind.ToString().ToLowerInvariant(), Default = f.Default, Help = f.Help,
        Group = f.Group, Choices = [.. f.Choices], Strict = f.Strict, Advanced = f.Advanced,
    };
}

public sealed class OpInfo
{
    public string Name { get; set; } = "";
    /// <summary>Default file name when exporting a preset.</summary>
    public string PresetFile { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Steps { get; set; } = [];
    /// <summary>The steps of the execution (step bar); empty for very fast operations.</summary>
    public List<string> Stages { get; set; } = [];
    public bool Mutates { get; set; }
    public string Category { get; set; } = "";
    /// <summary>Lock group (menu item): only one in the same group can run at a time.</summary>
    public string Lock { get; set; } = "";
    public List<FieldInfo> Fields { get; set; } = [];
}

public sealed class CheckInfo
{
    public string Item { get; set; } = "";
    public string State { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>The corresponding preset item; empty means the problem concerns the whole operation.</summary>
    public string Field { get; set; } = "";

    public static CheckInfo From(CheckItem c) => new() { Item = c.Item, State = c.StateText, Detail = c.Detail, Field = c.Field };
}

public sealed class CheckResult
{
    public Dictionary<string, string> Values { get; set; } = [];
    public List<FieldInfo> Fields { get; set; } = [];
    public List<CheckInfo> Checks { get; set; } = [];
    /// <summary>Attached data table (monitor layout).</summary>
    public List<string> ExtraHeaders { get; set; } = [];
    public List<List<string>> ExtraRows { get; set; } = [];
    public bool Passed { get; set; }
    public int Warns { get; set; }
}

public sealed class Request
{
    public Dictionary<string, string> Values { get; set; } = [];
}

public sealed class Event
{
    public long Seq { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Level { get; set; } = "";
    /// <summary>stage: which step, how many steps in total, description, whether skipped; result: Detail is the value; progress: percentage, -1 if none; checks: check results.</summary>
    public int No { get; set; }
    public int Total { get; set; }
    public string Detail { get; set; } = "";
    public bool Skipped { get; set; }
    public int Percent { get; set; } = -1;
    public List<CheckInfo> Checks { get; set; } = [];

    public static Event From(BufferedOutput.Item i) => new()
    {
        Seq = i.Seq, Kind = i.Kind, Text = i.Text, Level = i.Level.ToString().ToLowerInvariant(),
        No = i.No, Total = i.Total, Detail = i.Detail, Skipped = i.Skipped, Percent = i.Percent,
        Checks = i.Checks?.Select(CheckInfo.From).ToList() ?? [],
    };
}

public sealed class JobInfo
{
    public string Id { get; set; } = "";
    public string Op { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "";   // idle / running / done / failed
    public string Error { get; set; } = "";
    public string Started { get; set; } = "";
    /// <summary>Time elapsed so far (up to the end): the page uses it to compute the start time, independent of time zones and clocks on both sides.</summary>
    public long ElapsedMs { get; set; }
    public string LogPath { get; set; } = "";
    /// <summary>Cumulative count of job starts / ends (Jobs.Changes): the page uses it to know whether any job finished between two states.</summary>
    public long Changes { get; set; }
    public string Lock { get; set; } = "";
    /// <summary>Who is running it (frontend and user), which process; Local = in this webui process itself.</summary>
    public string Who { get; set; } = "";
    public int Pid { get; set; }
    public bool Local { get; set; }
    public List<Event> Events { get; set; } = [];
}

/// <summary>The state pushed by /api/status and /api/events: jobs running in all processes (at most one per lock group).</summary>
public sealed class StatusInfo
{
    public long Changes { get; set; }
    public List<JobInfo> Running { get; set; } = [];
    /// <summary>The git hash of this server exe.</summary>
    public string Hash { get; set; } = "";
}

public sealed class EntryInfo
{
    public string Id { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Vhd { get; set; } = "";
    public bool VhdExists { get; set; }
    public bool Hidden { get; set; }
    public bool IsDefault { get; set; }
    public bool IsBootNext { get; set; }
    public bool Ems { get; set; }
    public bool IsWlb { get; set; }
    public bool IsVhd { get; set; }
    /// <summary>wlb: full functionality; system: non-VHD system boot entry, can only be set as default / next boot; other: boot entry of another VHD, listed only.</summary>
    public string Tier { get; set; } = "";
    /// <summary>Status markers (hidden, default, next boot, ...), the same text as in the other frontends.</summary>
    public List<string> Flags { get; set; } = [];

    public static EntryInfo From(Bcd.Entry e) => new()
    {
        Id = e.Id, Desc = e.Desc, Vhd = e.Target, VhdExists = e.VhdExists, Hidden = e.Hidden,
        IsDefault = e.IsDefault, IsBootNext = e.IsBootNext, Ems = e.Ems, IsWlb = e.IsWlb, IsVhd = e.IsVhd, Tier = e.Tier, Flags = e.Flags(),
    };
}

public sealed class TreeNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";   // base / parent / child
    public bool ReadOnly { get; set; }
    public bool Attached { get; set; }
    public bool DirtyLog { get; set; }
    public double FileGB { get; set; }
    public double MaxGB { get; set; }
    public string Link { get; set; } = "";
    public bool Broken { get; set; }
    public string Mtime { get; set; } = "";
    /// <summary>Disk markers (base disk / child disk, read-only, size, ...), the same text as in the other frontends.</summary>
    public List<string> Tags { get; set; } = [];
    public List<EntryInfo> Entries { get; set; } = [];
    public List<TreeNode> Children { get; set; } = [];

    public static TreeNode From(Tree.Node n) => new()
    {
        Name = n.Disk.Name, Path = n.Disk.Path, Kind = n.Disk.IsBase ? "base" : n.Children.Count > 0 ? "parent" : "child",
        ReadOnly = n.Disk.ReadOnly, Attached = n.Disk.Attached, DirtyLog = n.Disk.Info is { LogGuid: var g } && g != System.Guid.Empty,
        FileGB = System.Math.Round(n.Disk.FileSize / (double)(1L << 30), 1), MaxGB = n.Disk.Info is null ? 0 : System.Math.Round(n.Disk.Info.VirtualSize / (double)(1L << 30)),
        Link = n.Link, Broken = n.Broken, Mtime = n.Disk.Mtime.ToString("yyyy-MM-dd HH:mm"), Tags = n.Tags(),
        Entries = n.Entries.ConvertAll(EntryInfo.From), Children = n.Children.ConvertAll(From),
    };
}

public sealed class Overview
{
    public List<TreeNode> Roots { get; set; } = [];
    public List<EntryInfo> Orphans { get; set; } = [];
    public List<EntryInfo> OtherEntries { get; set; } = [];   // Non-WLB boot entries, display only
    public string VhdDir { get; set; } = "";
    public long FreeGB { get; set; }
    public string Host { get; set; } = "";
    public bool OnWorkHost { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>Two lines of text for environment status and totals (OverviewData), the same as in the other frontends.</summary>
    public string Env { get; set; } = "";
    public string Totals { get; set; } = "";
}

public sealed class ConfigInfo
{
    public Dictionary<string, string> Values { get; set; } = [];
    /// <summary>Fields of the settings page (drawn the same way as the fields of operation pages).</summary>
    public List<FieldInfo> Fields { get; set; } = [];
    public List<string> Isos { get; set; } = [];
    public string Path { get; set; } = "";
    public Dictionary<string, string> Resolved { get; set; } = [];
}

public sealed class Error
{
    public string Message { get; set; } = "";
}
