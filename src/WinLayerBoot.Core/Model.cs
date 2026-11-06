using System;
using System.Collections.Generic;
using System.Linq;

namespace WinLayerBoot.Core;

public enum FieldKind { Text, Bool, Int, Choice, Path, Password, Note }

/// <summary>
/// One preset item. All four frontends draw from this description: the CLI makes it --key value, TUI/Web/WinUI make it an input box, switch or dropdown.
/// Default is computed by the operation from the values filled in so far (for example, once a base disk is chosen, the default child disk name follows).
/// </summary>
public sealed record Field(string Key, string Label, FieldKind Kind, string Default)
{
    public string Help { get; init; } = "";
    public string Group { get; init; } = "";
    /// <summary>Choice: the options; Path: optional candidates (can still be typed by hand).</summary>
    public string[] Choices { get; init; } = [];
    /// <summary>Choice: can only be chosen from Choices (the dropdown is not editable).</summary>
    public bool Strict { get; init; }
    public bool Advanced { get; init; }
}

/// <summary>Preset values: keys are case-insensitive, an empty value equals not filled.</summary>
public sealed class Values : Dictionary<string, string>
{
    public Values() : base(StringComparer.OrdinalIgnoreCase) { }
    public Values(IDictionary<string, string> src) : base(src, StringComparer.OrdinalIgnoreCase) { }

    public string Get(string key, string def = "") => TryGetValue(key, out var v) && v is not null ? v : def;
    public bool Has(string key) => TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);
    public bool Bool(string key, bool def = false) => Has(key) ? this[key].IsYes : def;
    public int Int(string key, int def = 0) => Has(key) && int.TryParse(this[key].Trim(), out var n) ? n : def;
    public Values Clone() => new(this);
}

public enum CheckState { Ok, Warn, Fail, Skip }

public sealed record CheckItem(string Item, CheckState State, string Detail)
{
    /// <summary>The preset item this one corresponds to (Operation.CheckFields): the UI shows the problem under that input box. Empty means a problem with the whole operation.</summary>
    public string Field { get; init; } = "";
    public string StateText => State switch { CheckState.Ok => "OK", CheckState.Warn => "Warning", CheckState.Fail => "Failed", _ => "Skipped" };
}

/// <summary>Pre-execution check table.</summary>
public sealed class CheckList : List<CheckItem>
{
    public void Add(string item, bool ok, string detail, bool warn = false, bool skip = false) =>
        Add(new CheckItem(item, skip ? CheckState.Skip : ok ? CheckState.Ok : warn ? CheckState.Warn : CheckState.Fail, detail));

    public void Dir(string item, string dir)
    {
        if (System.IO.Directory.Exists(dir)) Add(item, true, dir);
        else Add(item, false, $"{dir} does not exist, will be created on run", warn: true);
    }

    // ---- Check items shared by several operations

    public void Admin() => Add("Administrator rights", Files.IsAdmin(), "Must run as Administrator");

    public void WorkHost(Context ctx) => Add("Run on the work host", ctx.OnWorkHost(), "Run it in Server, not inside a child disk");

    /// <summary>The boot menu name must start with WLB-.</summary>
    public void EntryDesc(string desc, string item = "Boot entry name") =>
        Add(item, Bcd.IsWlbDesc(desc), Bcd.IsWlbDesc(desc) ? desc : $"{desc}: must start with {Config.EntryPrefix}");

    /// <summary>File name (without extension): non-empty, no illegal characters; with noBasePrefix, the prefix reserved for base disks cannot be used.</summary>
    public void FileName(string item, string name, bool noBasePrefix = false)
    {
        var basePre = noBasePrefix && name.StartsWith(Config.BasePrefix, StringComparison.OrdinalIgnoreCase);
        Add(item, name != "" && !basePre && Files.IsValidName(name), name == "" ? "Not set" : basePre ? $"{Config.BasePrefix} is a prefix reserved for base disks" : name);
    }

    /// <summary>The file to be created does not exist yet.</summary>
    public void NewFile(string item, string path, string ifExists = "use another name") =>
        Add(item, !System.IO.File.Exists(path), System.IO.File.Exists(path) ? $"{path} already exists, {ifExists}" : $"{path} does not exist, can be created");

    public void FreeSpace(string dir, long needGB, string detail, bool warn = false)
    {
        var free = Files.FreeGB(dir);
        Add($"Free space ≥ {needGB} GB", free >= needGB, $"{free} GB ({detail})", warn: warn);
    }

    public int Fails => this.Count(c => c.State == CheckState.Fail);
    public int Warns => this.Count(c => c.State == CheckState.Warn);
    public bool Passed => Fails == 0;
    public IEnumerable<string[]> Rows => this.Select(c => new[] { c.Item, c.StateText, c.Detail });
    public static readonly string[] Headers = ["Check", "Result", "Description"];

    /// <summary>The check table and its attached tables laid out as plain text (for the command line and logs).</summary>
    public List<string> TextLines() => [.. TextTable.Format(Headers, Rows), .. Extra is null ? [] : TextTable.Format(Extra.Headers, Extra.Rows)];

    /// <summary>Data tables attached besides the check table (for example the monitor layout), drawn as tables by the frontend.</summary>
    public DataTable? Extra { get; set; }
    public sealed record DataTable(string[] Headers, List<string[]> Rows);

    /// <summary>Items with problems (failures first): listed directly in the UI; those without problems are collapsed.</summary>
    public IEnumerable<CheckItem> Problems => this.Where(c => c.State == CheckState.Fail).Concat(this.Where(c => c.State == CheckState.Warn));
    public IEnumerable<CheckItem> Fine => this.Where(c => c.State is CheckState.Ok or CheckState.Skip);

    /// <summary>The most severe problem on a preset item.</summary>
    public CheckItem? ProblemOf(string field) => Problems.FirstOrDefault(c => c.Field.EqualsIgnoreCase(field));

    /// <summary>Tags each item with its preset item according to the mapping table the operation gives.</summary>
    public void Bind(IReadOnlyDictionary<string, string> fields)
    {
        for (var i = 0; i < Count; i++)
            if (fields.TryGetValue(this[i].Item, out var f)) this[i] = this[i] with { Field = f };
    }
}

/// <summary>
/// One operation: base disk, child disk, snapshot, export, save display, boot entry management...
/// Three steps: Fields gives preset items and defaults -> Check gives the check table -> Run executes. Shared by all frontends.
/// </summary>
public abstract class Operation
{
    /// <summary>Name on the command line, also the op in preset files.</summary>
    public abstract string Name { get; }
    public abstract string Title { get; }
    public abstract string Summary { get; }
    /// <summary>What it does (help page and UI description).</summary>
    public virtual string[] Steps => [];
    /// <summary>The steps execution is divided into: Run calls ctx.Stage(name) in order, and the UI draws the whole step bar from this before starting. Very fast operations have no steps.</summary>
    public virtual string[] Stages => [];
    /// <summary>Mapping from check items to preset items: the UI shows the problem under the corresponding input box.</summary>
    public virtual IReadOnlyDictionary<string, string> CheckFields { get; } = new Dictionary<string, string>();
    /// <summary>true: execution changes the system (BCD, disks), so the UI asks for confirmation.</summary>
    public virtual bool Mutates => true;
    /// <summary>Group in the left navigation: build, manage.</summary>
    public virtual string Category => "build";
    /// <summary>Lock group, i.e. the menu item in the navigation: only one in the same group can execute at a time (across processes and frontends), different groups do not affect each other. The boot entry management operations share "entries".</summary>
    public virtual string Lock => Name;

    public abstract IReadOnlyList<Field> Fields(Context ctx, Values v);
    public abstract CheckList Check(Context ctx, Values v);
    public abstract void Run(Context ctx, Values v);

    /// <summary>
    /// Fills in defaults for unfilled items. A default may depend on other values (the default child disk name is known only once a base disk is chosen), so it computes twice until stable.
    /// Returns the completed values and the final field table.
    /// </summary>
    public (Values Values, IReadOnlyList<Field> Fields) Resolve(Context ctx, Values v)
    {
        var cur = v.Clone();
        IReadOnlyList<Field> fields = [];
        for (var i = 0; i < 3; i++)
        {
            fields = Fields(ctx, cur);
            var changed = false;
            foreach (var f in fields)
            {
                if (f.Kind == FieldKind.Note) continue;
                if (!cur.Has(f.Key)) { cur[f.Key] = f.Default; changed = true; }
            }
            if (!changed) break;
        }
        return (cur, fields);
    }

    /// <summary>Keys present in the values but not in the field table (misspelled or removed): the check table gives a warning.</summary>
    public static void WarnUnknownKeys(CheckList checks, Values v, IReadOnlyList<Field> fields)
    {
        var unknown = v.Keys.Where(k => !fields.Any(f => f.Key.EqualsIgnoreCase(k))).ToList();
        if (unknown.Count > 0) checks.Add("Keys in preset", false, $"Unused: {string.Join(", ", unknown)} (misspelled key, or this item has been removed)", warn: true);
    }

    public sealed record Prepared(Values Values, IReadOnlyList<Field> Fields, CheckList Checks);

    /// <summary>Fill defaults, check, flag unused keys: the UI goes through this on every value change and before a job starts. An error thrown by a check also counts as one failed item, and the form shows it as usual.</summary>
    public Prepared Prepare(Context ctx, Values v)
    {
        var (vals, fields) = Resolve(ctx, v);
        CheckList checks;
        try { checks = Check(ctx, vals); }
        catch (WlbError e) { checks = []; checks.Add("Check", false, e.Message); }
        WarnUnknownKeys(checks, vals, fields);
        checks.Bind(CheckFields);
        return new(vals, fields, checks);
    }

    /// <summary>Executes, and writes a log at the same time (%TEMP%\WLB-*.log).</summary>
    public void Execute(Context ctx, Values vals)
    {
        ctx.Out.StartLog(ctx.LogPath);
        ctx.Out.Plan = Stages;
        try { Run(ctx, vals); }
        finally { ctx.Out.StopLog(); }
    }

    /// <summary>Exports the preset: all values in the field table except descriptions.</summary>
    public Preset ToPreset(Values vals, IReadOnlyList<Field> fields, string by)
    {
        var p = new Preset { Op = Name, Note = $"{Title}, exported by {by}" };
        // Preset files get copied around, so the password is not written in; on import the password takes the default from settings
        foreach (var f in fields.Where(f => f.Kind is not (FieldKind.Note or FieldKind.Password))) p.Values[f.Key] = vals.Get(f.Key);
        return p;
    }

    /// <summary>Imports a preset: it must be for this operation.</summary>
    public Values FromPreset(Preset p)
    {
        if (p.Op != "" && !p.Op.EqualsIgnoreCase(Name)) throw new WlbError($"This is the preset for \"{p.Op}\", not for \"{Name}\"");
        return new Values(p.Values);
    }
}

/// <summary>Registry of all operations.</summary>
public static class Operations
{
    private static List<Operation>? _all;

    public static IReadOnlyList<Operation> All => _all ??=
    [
        new Ops.BaseOp(),
        new Ops.LayerOp(),
        new Ops.SnapshotOp(),
        new Ops.ExportOp(),
        new Ops.DisplaySaveOp(),
        new Ops.EntryAddOp(),
        new Ops.EntryRenameOp(),
        new Ops.EntryDeleteOp(),
        new Ops.EntryHideOp(),
        new Ops.EntryShowOp(),
        new Ops.EntryNextOp(),
        new Ops.EntryDefaultOp(),
    ];

    public static Operation? Find(string name) => All.FirstOrDefault(o => o.Name.EqualsIgnoreCase(name));
}
