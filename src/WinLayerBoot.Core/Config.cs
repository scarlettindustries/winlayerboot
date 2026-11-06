using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace WinLayerBoot.Core;

/// <summary>
/// Central settings: root directory, LOF / virtio ISO, boot menu language, account defaults (the install ISO is a preset item of the base disk). Stored in %ProgramData%\WinLayerBoot\config.json,
/// and all four frontends read and write the same file; the CLI can point to another file with --config, or with the environment variable WLB_CONFIG.
/// </summary>
public sealed class Config
{
    /// <summary>Root directory of materials and products: iso\, drivers\&lt;machine name&gt;\, display\&lt;profile name&gt;\, vhd\, export\, mnt\.</summary>
    public string Root { get; set; } = @"V:\WLB";

    /// <summary>Languages and Optional Features ISO (needed to add SAC to the client and for the SSH server); empty means auto-find *LOF*.</summary>
    public string LofIso { get; set; } = "";

    /// <summary>virtio-win driver ISO; empty means auto-find *virtio*.</summary>
    public string VirtioIso { get; set; } = "";

    /// <summary>Boot menu language: used by bcdboot /l, and {bootmgr} and the boot entries created by this tool are also set to it (if not set, bcdboot would change them to the base disk language).</summary>
    public string BootLocale { get; set; } = "en-US";

    /// <summary>Common boot menu languages (language packs shipped with the Windows boot files).</summary>
    public static readonly string[] BootLocales = ["en-US", "en-GB", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "it-IT", "pt-BR", "ru-RU"];

    // Account defaults: empty in the source, filled in by the user in the settings and stored in the local config.json (not in the repo); automatically filled into the form when creating a child disk
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";
    public string PubKey { get; set; } = "";

    /// <summary>Default port of the web interface.</summary>
    public int WebPort { get; set; } = 5080;

    // ---- Fixed conventions
    public const string EntryPrefix = "WLB-";        // This tool handles only boot entries whose description starts with WLB-
    public const string BasePrefix = "WLB-stage1-";  // Base disk file name prefix

    [JsonIgnore] public string VhdDir => Path.Combine(Root, "vhd");
    [JsonIgnore] public string IsoDir => Path.Combine(Root, "iso");
    [JsonIgnore] public string DriversDir => Path.Combine(Root, "drivers");
    [JsonIgnore] public string DisplayDir => Path.Combine(Root, "display");
    [JsonIgnore] public string ExportDir => Path.Combine(Root, "export");
    [JsonIgnore] public string MntDir => Path.Combine(Root, "mnt");

    /// <summary>The disk with this name in the vhd directory (with or without .vhdx in the name).</summary>
    public string VhdPath(string name) => Path.Combine(VhdDir, name.StripVhdx() + ".vhdx");
    public string ExportPath(string name) => Path.Combine(ExportDir, name.StripVhdx() + ".vhdx");

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("WLB_CONFIG") is { Length: > 0 } e ? e
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinLayerBoot", "config.json");

    [JsonIgnore] public string LoadedFrom { get; private set; } = "";

    public static Config Load(string? path = null)
    {
        path ??= DefaultPath;
        Config c;
        if (File.Exists(path))
        {
            try { c = JsonSerializer.Deserialize(File.ReadAllText(path), WlbJson.Default.Config) ?? new Config(); }
            catch (Exception e) { throw new WlbError($"cannot read settings file {path}: {e.Message}"); }
        }
        else c = new Config();
        c.LoadedFrom = path;
        c.TryCreateDirs();
        return c;
    }

    /// <summary>
    /// The root directory and iso, drivers, display, vhd, export under it: at startup, when reading the settings, an attempt is made to create them; if that fails (disk missing, no permission), leave it.
    /// mnt is not created: it is created at mount time and deleted when empty after use (see Mounts).
    /// </summary>
    public void TryCreateDirs()
    {
        foreach (var d in new[] { Root, IsoDir, DriversDir, DisplayDir, VhdDir, ExportDir })
            try { Directory.CreateDirectory(d); } catch { }
    }

    /// <summary>
    /// Changes several settings and saves: when several programs and several people have it open at once, the file is re-read inside the lock and only these items are changed, so other items someone else just saved are not overwritten.
    /// If a value is invalid, nothing is changed (changes are made on a copy first).
    /// </summary>
    public void Update(IEnumerable<KeyValuePair<string, string>> changes)
    {
        using var _ = GlobalLock.Take("config", "Write settings file");
        var c = File.Exists(LoadedFrom) ? Load(LoadedFrom) : new Config { LoadedFrom = LoadedFrom };
        foreach (var (k, v) in changes) c.Set(k, v);
        c.Save();
        CopyFrom(c);
    }

    private void CopyFrom(Config o)
    {
        Root = o.Root; LofIso = o.LofIso; VirtioIso = o.VirtioIso; BootLocale = o.BootLocale;
        User = o.User; Pass = o.Pass; PubKey = o.PubKey; WebPort = o.WebPort;
    }

    /// <summary>The settings file was changed (by another program) and has been re-read: raised on a background thread.</summary>
    public event Action? Changed;
    private FileSystemWatcher? _watcher;
    private Timer? _reload;

    /// <summary>Called by long-running frontends: re-reads the settings file if it changed, so the settings seen by the frontends stay consistent.</summary>
    public void Watch()
    {
        if (_watcher is not null || LoadedFrom == "") return;
        try
        {
            var dir = Path.GetDirectoryName(LoadedFrom)!;
            Directory.CreateDirectory(dir);
            _reload = new Timer(_ =>
            {
                try { CopyFrom(Load(LoadedFrom)); }
                catch { return; }   // Being written right now: read again on the next change
                Changed?.Invoke();
            });
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(LoadedFrom)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            _watcher.Changed += (_, _) => _reload.Change(300, Timeout.Infinite);
            _watcher.Created += (_, _) => _reload.Change(300, Timeout.Infinite);
            _watcher.Renamed += (_, _) => _reload.Change(300, Timeout.Infinite);
        }
        catch { }
    }

    public void Save(string? path = null)
    {
        path ??= LoadedFrom is { Length: > 0 } ? LoadedFrom : DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WlbJson.Default.Config), Files.Utf8Bom);
        LoadedFrom = path;
    }

    /// <summary>Settable keys (for the config command and the settings page).</summary>
    public static readonly ConfigKey[] Keys =
    [
        new("Root", "Root folder", "Root folder for source material and output (iso, drivers, display, vhd, export, mnt are all below it); must be on NTFS"),
        new("LofIso", "LOF ISO", "Languages and Optional Features ISO; empty = auto-detect *LOF*", FieldKind.Choice),
        new("VirtioIso", "virtio ISO", "virtio-win driver ISO; empty = auto-detect *virtio*", FieldKind.Choice),
        new("BootLocale", "Boot menu language", "Language shown in the boot menu; when adding a boot entry, taking a snapshot or exporting, {bootmgr} and the new boot entry are set to it", FieldKind.Choice),
        new("User", "Default username", "Auto-filled when creating a child disk; local administrator of the child disk (fixed to Administrator for Server base disks)"),
        new("Pass", "Default password", "Auto-filled when creating a child disk; written to the unattend file in plain text", FieldKind.Password),
        new("PubKey", "SSH public key", "Auto-filled when creating a child disk; written to administrators_authorized_keys"),
        new("WebPort", "Web port", "Default port of the webui command (restart webui after changing it)", FieldKind.Int),
    ];

    /// <summary>Fields of the settings page (the four frontends draw them the same way as operation forms): the current value is the default; the ISO candidates are the files in the iso directory, and the boot menu language can only be chosen from the list.</summary>
    public List<Field> Fields()
    {
        var isos = AllIsos().ToArray();
        return Keys.Select(k => new Field(k.Key, k.Label, k.Kind, Get(k.Key))
        {
            Help = k.Help,
            Choices = k.Key == "BootLocale" ? BootLocales : k.Key.EndsWith("Iso") ? isos : [],
            Strict = k.Key == "BootLocale",
        }).ToList();
    }

    public string Get(string key) => key.ToLowerInvariant() switch
    {
        "root" => Root, "lofiso" => LofIso, "virtioiso" => VirtioIso, "bootlocale" => BootLocale,
        "user" => User, "pass" => Pass, "pubkey" => PubKey, "webport" => WebPort.ToString(),
        _ => throw new WlbError($"No such setting: {key}"),
    };

    public void Set(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "root": Root = value.TrimEnd('\\'); break;
            case "lofiso": LofIso = value; break;
            case "virtioiso": VirtioIso = value; break;
            case "bootlocale":
                var m = Regex.Match(value.Trim(), @"^([A-Za-z]{2,3})-([A-Za-z]{2,4})$");
                BootLocale = m.Success ? $"{m.Groups[1].Value.ToLowerInvariant()}-{m.Groups[2].Value.ToUpperInvariant()}" : throw new WlbError($"BootLocale must be a language code like en-US, got \"{value}\"");
                break;
            case "user": User = value; break;
            case "pass": Pass = value; break;
            case "pubkey": PubKey = value; break;
            case "webport": WebPort = int.TryParse(value, out var p) && p is > 0 and < 65536 ? p : throw new WlbError($"WebPort must be a port number from 1–65535, got \"{value}\""); break;
            default: throw new WlbError($"No such setting: {key}");
        }
    }

    // ---- ISO resolution: use the one given in the settings, otherwise search the iso directory with wildcards

    /// <summary>The install ISO is not in the settings but a preset item of the base disk; only its default value is given here.</summary>
    public string FindInstallIso() => FindIso(["*windows_11*", "*windows_server*", "*windows*"], "*LOF*");
    public string ResolveLofIso() => LofIso != "" ? LofIso : FindIso(["*LOF*"], "");
    public string ResolveVirtioIso() => VirtioIso != "" ? VirtioIso : FindIso(["*virtio*"], "");

    /// <summary>All ISOs in the iso directory (for the selection box).</summary>
    public List<string> AllIsos() =>
        Directory.Exists(IsoDir) ? Directory.GetFiles(IsoDir, "*.iso").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : [];

    private string FindIso(string[] patterns, string exclude)
    {
        var all = AllIsos();
        foreach (var p in patterns)
        {
            var f = all.FirstOrDefault(x => Path.GetFileName(x).Like(p) && (exclude == "" || !Path.GetFileName(x).Like(exclude)));
            if (f is not null) return f;
        }
        return "";
    }
}

/// <summary>A setting item: key name, display name, description, and how it is drawn on the settings page.</summary>
public sealed record ConfigKey(string Key, string Label, string Help, FieldKind Kind = FieldKind.Text);

/// <summary>The preset of one operation, which can be exported to a file and imported in the four frontends.</summary>
public sealed class Preset
{
    public string Op { get; set; } = "";
    public string Note { get; set; } = "";
    public string Saved { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default name of the exported file: includes the git hash at build time, so the file name shows which version exported it.</summary>
    public static string FileName(string op) => $"wlb-{op}-{BuildInfo.Hash}.json";

    /// <summary>Reads the preset file written in the child disk root directory when the child disk was created: mounted read-only, unmounted after reading.</summary>
    public static Preset FromVhd(Context ctx, string vhdx, string op) => ctx.Mnt.WithWindows(vhdx, readOnly: true, "preset", (dir, _) =>
    {
        var file = new DirectoryInfo(dir + @"\").GetFiles($"wlb-{op}-*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            ?? throw new WlbError($@"no wlb-{op}-*.json in the root folder of {Path.GetFileName(vhdx)}: this disk may have been created before this feature existed, or 'Save preset on disk' was turned off at the time");
        return Load(file.FullName);
    }, quiet: true);

    public static Preset Load(string path)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), WlbJson.Default.Preset) ?? throw new WlbError($"{path} is empty"); }
        catch (WlbError) { throw; }
        catch (Exception e) { throw new WlbError($"cannot read preset file {path}: {e.Message}"); }
    }

    public void Save(string path)
    {
        Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(), Files.Utf8Bom);
    }

    public string ToJson() => JsonSerializer.Serialize(this, WlbJson.Default.Preset);

    public static Preset FromJson(string json)
    {
        try { return JsonSerializer.Deserialize(json, WlbJson.Default.Preset) ?? throw new WlbError("preset is empty"); }
        catch (WlbError) { throw; }
        catch (Exception e) { throw new WlbError($"cannot read preset: {e.Message}"); }
    }
}

/// <summary>JSON source generation context: single-file trimmed publishing cannot rely on reflection serialization.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(Preset))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Api.OpInfo))]
[JsonSerializable(typeof(List<Api.OpInfo>))]
[JsonSerializable(typeof(Api.FieldInfo))]
[JsonSerializable(typeof(List<Api.FieldInfo>))]
[JsonSerializable(typeof(Api.CheckInfo))]
[JsonSerializable(typeof(Api.CheckResult))]
[JsonSerializable(typeof(Api.JobInfo))]
[JsonSerializable(typeof(Api.StatusInfo))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(Api.Event))]
[JsonSerializable(typeof(List<Api.Event>))]
[JsonSerializable(typeof(Api.TreeNode))]
[JsonSerializable(typeof(Api.Overview))]
[JsonSerializable(typeof(Api.EntryInfo))]
[JsonSerializable(typeof(List<Api.EntryInfo>))]
[JsonSerializable(typeof(Api.ConfigInfo))]
[JsonSerializable(typeof(Api.Request))]
[JsonSerializable(typeof(Api.Error))]
public partial class WlbJson : JsonSerializerContext;
