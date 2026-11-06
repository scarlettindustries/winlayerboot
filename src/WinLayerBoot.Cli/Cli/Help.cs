using System;
using System.Linq;
using System.Reflection;
using System.Text;
using WinLayerBoot.Core;

namespace WinLayerBoot.Cli;

public static class Help
{
    public static string Version =>
        typeof(Help).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Help).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Usage
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"""
                WinLayerBoot (wlb) {Version} - Native boot from layered VHDX on Windows

                Usage
                  wlb <command> [--key value]... [--check] [-y] [-i] [--load file] [--save file]
                  wlb gui                      WinUI window (other arguments are ignored when gui is given)
                  wlb                          no arguments: same as gui when double-clicked, prints this help from a terminal
                  wlb webui [--port N]         Web UI (default port comes from Settings, 5080)
                  wlb tui                      TUI
                  wlb tree                     inheritance tree: disks in the vhd folder arranged by parent/child, with their boot entries
                  wlb entries                  list boot entries starting with WLB
                  wlb config [key=value ...]   view or change central settings (root folder, three ISOs, account defaults)
                  wlb help [command]           options and defaults of a command
                  wlb selftest                 self-test: whether WMI and the APIs work in this exe (does not change system settings)

                Create

                """);
            foreach (var o in Operations.All.Where(o => o.Category == "build")) sb.AppendLine($"  wlb {o.Name,-14} {o.Title}: {o.Summary}");
            sb.AppendLine();
            sb.AppendLine("Boot entry management (only boot entries starting with WLB- are handled)");
            foreach (var o in Operations.All.Where(o => o.Category == "manage")) sb.AppendLine($"  wlb {o.Name,-14} {o.Title}: {o.Summary}");
            sb.Append("""

                Switches
                  --key value  option; key names in wlb help <command>; boolean options take Y/N
                  --check      only set options and check, do not run (exit code 1 if a check fails)
                  -y           skip the final confirmation before running
                  -i           ask for each option (Enter for default)
                  --load file  import a preset file (JSON exported from the UI; --key value on the command line takes precedence)
                  --from-vhd disk  layer: import the settings used when this child disk was created (child disk name and boot menu name are recalculated)
                  --save file  export the final settings of this run to a file, importable in all four interfaces; no password included, the default from Settings is used on import
                  --config file  use another settings file (default %ProgramData%\WinLayerBoot\config.json)

                Examples
                  wlb base --Edition ProfessionalWorkstation --WriteHash Y
                  wlb layer --check
                  wlb layer --Base WLB-stage1-win11-26200-pws-en-us --Child mypc --Drivers Y --Machine mypc -y
                  wlb layer --load mypc.json -y
                  wlb snapshot --Parent mypc
                  wlb export --Entry "WLB-mypc (Win11 PWS en-US)" --ExpandGB 64
                  wlb entry-rename --Entry "WLB-mypc (Win11 PWS en-US)" --Desc "WLB-mypc work PC"

                The install ISO is a base disk setting (wlb base --Iso <file>); LOF and virtio are in Settings (wlb config). Any not set are searched by wildcard in <root folder>\iso.
                Log %TEMP%\WLB-<time>-<pid>.log

                """);
            return sb.ToString();
        }
    }

    /// <summary>The preset items of a command: defaults computed from the current environment.</summary>
    public static string For(string name)
    {
        var op = Operations.Find(name);
        if (op is null) return $"Unknown command \"{name}\".\n\n{Usage}";
        var sb = new StringBuilder();
        sb.AppendLine($"wlb {op.Name} —— {op.Title}");
        sb.AppendLine($"  {op.Summary}");
        if (op.Steps.Length > 0)
        {
            sb.AppendLine("What it does");
            for (var i = 0; i < op.Steps.Length; i++) sb.AppendLine($"  {i + 1}. {op.Steps[i]}");
        }
        sb.AppendLine("Options (--key value = current default)");
        try
        {
            using var ctx = new Context(Config.Load(), new NullOutput());
            var (vals, fields) = op.Resolve(ctx, new Values());
            string group = "";
            foreach (var f in fields)
            {
                if (f.Group != group) { group = f.Group; if (group != "") sb.AppendLine($"  —— {group}"); }
                if (f.Kind == FieldKind.Note) { sb.AppendLine($"  {TextTable.Pad(f.Label, 12)} {f.Default}"); continue; }
                var def = f.Kind == FieldKind.Password ? "***" : vals.Get(f.Key);
                sb.AppendLine($"  --{TextTable.Pad(f.Key, 12)} = {def}");
                if (f.Help != "") sb.AppendLine($"      {f.Help}");
                if (f.Choices.Length > 0) sb.AppendLine($"      Choices: {string.Join(", ", f.Choices)}");
            }
            ctx.Cleanup();
        }
        catch (Exception e) { sb.AppendLine($"  (error computing defaults: {e.Message})"); }
        return sb.ToString();
    }
}
