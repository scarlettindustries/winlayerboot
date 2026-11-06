using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;

namespace WinLayerBoot.Cli;

/// <summary>
/// CLI: wlb &lt;command&gt; [--key value]... [--check] [-y] [-i] [--load file] [--save file] [--config file]
/// </summary>
public static class CliApp
{
    private sealed class Args
    {
        public string Command = "";
        public Values Values = new();
        public List<string> Positional = [];
        public bool Check, Yes, Interactive;
        public string? Load, FromVhd, Save, Config;
        public int? Port;
    }

    private static Args Parse(string[] args)
    {
        var a = new Args();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-')) { a.Command = args[0].ToLowerInvariant(); i = 1; }
        for (; i < args.Length; i++)
        {
            var s = args[i];
            if (!s.StartsWith('-')) { a.Positional.Add(s); continue; }
            var key = s.TrimStart('-');
            string? val = null;
            var eq = key.IndexOf('=');
            if (eq > 0) { val = key[(eq + 1)..]; key = key[..eq]; }
            switch (key.ToLowerInvariant())
            {
                case "check": case "checkonly": a.Check = true; break;
                case "y": case "yes": a.Yes = true; break;
                case "i": case "interactive": a.Interactive = true; break;
                case "h": case "help": case "?": a.Command = a.Command == "" ? "help" : a.Command; a.Positional.Insert(0, "--help"); break;
                case "load": a.Load = val ?? Next(args, ref i, s); break;
                case "from-vhd": a.FromVhd = val ?? Next(args, ref i, s); break;
                case "save": a.Save = val ?? Next(args, ref i, s); break;
                case "config": a.Config = val ?? Next(args, ref i, s); break;
                case "port": a.Port = int.TryParse(val ?? Next(args, ref i, s), out var p) ? p : throw new WlbError("--port must be a port number"); break;
                default:
                    a.Values[key] = val ?? Next(args, ref i, s);
                    break;
            }
        }
        return a;
    }

    private static string Next(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length) throw new WlbError($"{opt} is missing a value");
        return args[++i];
    }

    public static int Run(string[] rawArgs)
    {
        Args a;
        try { a = Parse(rawArgs); }
        catch (WlbError e) { ConsoleWrite.Write($"Error: {e.Message}\n\n{Help.Usage}"); return 2; }

        if (a.Command is "" or "help" or "-h" or "--help")
        {
            var topic = a.Positional.FirstOrDefault(p => !p.StartsWith('-'));
            ConsoleWrite.Write(topic is null ? Help.Usage : Help.For(topic));
            return 0;
        }
        if (a.Command is "version" or "v") { ConsoleWrite.Write($"WinLayerBoot {Help.Version}\n"); return 0; }

        Config cfg;
        try { cfg = Config.Load(a.Config); }
        catch (WlbError e) { ConsoleWrite.Write($"Error: {e.Message}\n"); return 1; }

        using var ctx = new Context(cfg, new ConsoleOutput());
        try
        {
            try { ctx.Mnt.CleanStale(); } catch { }   // Every command first cleans up mount points left by a previous abnormal exit
            switch (a.Command)
            {
                case "selftest": return SelfTest.Run(ctx);
                case "config": return ConfigCmd(ctx, a);
                case "tree": return TreeCmd(ctx);
                case "entries": return EntriesCmd(ctx);
                case "ops": foreach (var o in Operations.All) ctx.Say($"  {o.Name,-14} {o.Title}: {o.Summary}"); return 0;
            }
            var op = Operations.Find(a.Command);
            if (op is null) { ConsoleWrite.Write($"Error: unknown command \"{a.Command}\"\n\n{Help.Usage}"); return 2; }
            if (a.Positional.Count > 0) { ConsoleWrite.Write($"Error: unexpected argument \"{a.Positional[0]}\"; set options as --key value\n"); return 2; }
            return RunOp(ctx, op, a);
        }
        catch (WlbError e) { ctx.Say($"Error: {e.Message}", Level.Error); return 1; }
        catch (Exception e) { ctx.Say($"Error: {e.Message}", Level.Error); ctx.Say(e.ToString(), Level.Dim); return 1; }
        finally { ctx.Cleanup(); }
    }

    // ---------------------------------------------------------------- operations

    private static int RunOp(Context ctx, Operation op, Args a)
    {
        var values = new Values();
        if (a.Load is not null)
        {
            var p = Preset.Load(a.Load);
            values = op.FromPreset(p);
            ctx.Say($"  Loaded preset {a.Load}{(p.Note != "" ? $" ({p.Note})" : "")}", Level.Dim);
        }
        if (a.FromVhd is not null)
        {
            if (op is not Ops.LayerOp layer) throw new WlbError("--from-vhd can only be used with layer");
            values = layer.FromVhd(ctx, ctx.Cfg.VhdPath(a.FromVhd));
            ctx.Say($"  Imported configuration from {a.FromVhd}", Level.Dim);
        }
        foreach (var (k, v) in a.Values) values[k] = v;   // Command-line override file

        ctx.Title($"{op.Title} - Preset");
        if (a.Interactive) values = Ask(ctx, op, values);
        var (vals, fields) = op.Resolve(ctx, values);
        foreach (var f in fields)
        {
            var src = a.Values.ContainsKey(f.Key) ? "   (command line)" : (a.Load ?? a.FromVhd) is not null && values.ContainsKey(f.Key) ? "   (preset file)" : "";
            var shown = f.Kind == FieldKind.Note ? f.Default : f.Kind == FieldKind.Password ? new string('*', Math.Min(8, vals.Get(f.Key).Length)) : vals.Get(f.Key);
            ctx.Say($"  {TextTable.Pad(f.Label, 34)} = {shown}{src}");
        }

        if (a.Save is not null)
        {
            op.ToPreset(vals, fields, "wlb").Save(a.Save);
            ctx.Say($"  Preset saved to {a.Save} (import it with --load, or in the Web UI, TUI or window)", Level.Ok);
        }

        var checks = op.Check(ctx, vals);
        Operation.WarnUnknownKeys(checks, vals, fields);
        ctx.Title("Pre-run checks");
        ctx.Out.Checks(checks);
        if (checks.Fails > 0) { ctx.Say($"{checks.Fails} check(s) failed; stopped.", Level.Error); return 1; }
        if (checks.Warns > 0) ctx.Say($"{checks.Warns} warning(s).", Level.Warn);
        if (a.Check) { ctx.Say("--check: checks only, nothing is run.", Level.Ok); return 0; }
        if (!a.Yes)
        {
            // Y or N must be typed explicitly; a bare Enter does not count, to prevent running by mistake while pressing Enter through
            string ans;
            do
            {
                var r = ConsoleWrite.ReadLine("Start? Enter Y or N (Enter alone is not accepted): ");
                if (r is null) { ctx.Say("No input (input stream ended); cancelled.", Level.Warn); return 1; }
                ans = r.Trim();
            } while (!Regex.IsMatch(ans, "^(y|yes|n|no)$", RegexOptions.IgnoreCase));
            if (ans.StartsWith('n') || ans.StartsWith('N')) { ctx.Say("Cancelled.", Level.Warn); return 1; }
        }
        Jobs.RunHere(ctx, op, vals);
        return 0;
    }

    /// <summary>-i: ask item by item. The field table changes with the answers (the child disk default name is known only after the base disk is chosen), so it is recomputed after each answer.</summary>
    private static Values Ask(Context ctx, Operation op, Values given)
    {
        var cur = given.Clone();
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ctx.Say("  (press Enter for default)");
        while (true)
        {
            var fields = op.Fields(ctx, cur);
            var f = fields.FirstOrDefault(x => !asked.Contains(x.Key));
            if (f is null) break;
            asked.Add(f.Key);
            if (f.Kind == FieldKind.Note) { ctx.Say($"  {f.Label}: {f.Default}", Level.Dim); continue; }
            if (given.Has(f.Key)) { ctx.Say($"  {f.Label} = {given[f.Key]}   (command line)"); continue; }
            var def = cur.Has(f.Key) ? cur[f.Key] : f.Default;
            if (f.Help != "") ctx.Say($"    {f.Help}", Level.Dim);
            if (f.Choices.Length > 0) ctx.Say($"    Choices: {string.Join(", ", f.Choices)}", Level.Dim);
            var prompt = f.Kind == FieldKind.Bool ? $"  {f.Label} (Y/N) [{def}]: " : $"  {f.Label} [{def}]: ";
            var ans = ConsoleWrite.ReadLine(prompt);
            if (ans is null) throw new WlbError("Input stream ended");
            ans = ans.Trim();
            cur[f.Key] = ans == "" ? def : ans;
        }
        return cur;
    }

    // ---------------------------------------------------------------- other commands

    private static int ConfigCmd(Context ctx, Args a)
    {
        var cfg = ctx.Cfg;
        var sets = a.Positional.Where(p => p.Contains('=')).ToList();
        foreach (var (k, v) in a.Values) sets.Add($"{k}={v}");
        if (sets.Count == 0)
        {
            ctx.Say($"Settings file: {cfg.LoadedFrom}{(File.Exists(cfg.LoadedFrom) ? "" : " (not saved yet; defaults shown below)")}");
            ctx.Out.Table(["Key", "Value", "Description"], Config.Keys.Select(k => new[] { k.Key, k.Key == "Pass" ? "***" : cfg.Get(k.Key), k.Help }));
            ctx.Say("ISOs actually used (empty entries are searched by wildcard in the iso folder):");
            ctx.Say($"  LOF ISO    {Or(cfg.ResolveLofIso())}");
            ctx.Say($"  virtio ISO {Or(cfg.ResolveVirtioIso())}");
            ctx.Say("Change: wlb config key=value ...   e.g. wlb config Root=V:\\WLB VirtioIso=V:\\WLB\\iso\\virtio-win.iso; the install ISO is a base disk setting: wlb base --Iso <file>");
            return 0;
        }
        var changes = sets.Select(s => { var eq = s.IndexOf('='); return new KeyValuePair<string, string>(s[..eq].Trim(), s[(eq + 1)..].Trim().Trim('"')); }).ToList();
        cfg.Update(changes);
        foreach (var (k, _) in changes) ctx.Say($"  {k} = {cfg.Get(k)}");
        ctx.Say($"Saved to {cfg.LoadedFrom}", Level.Ok);
        return 0;

        static string Or(string s) => s == "" ? "(not found)" : s;
    }

    private static int TreeCmd(Context ctx)
    {
        ctx.Say($"Inheritance tree: {ctx.Cfg.VhdDir} (only boot entries starting with {Config.EntryPrefix} are listed)", Level.Title);
        ctx.Say("");
        var d = OverviewData.Load(ctx);
        if (d.Tree.All.Count == 0) ctx.Say("  (no VHDX in the folder)", Level.Warn);
        foreach (var l in d.Tree.Render()) ctx.Say(l, l.Contains('⚠') ? Level.Warn : Level.Info);
        ctx.Say("");
        ctx.Say(d.Totals, Level.Dim);
        ctx.Say(d.Env, Level.Dim);
        return 0;
    }

    private static int EntriesCmd(Context ctx)
    {
        var g = EntryGroups.Load(ctx);
        ctx.Say($"{Config.EntryPrefix} boot entries (the only ones this tool handles):", Level.Title);
        if (g.Wlb.Count == 0) ctx.Say("  (none)");
        else ctx.Out.Table(["Boot entry", "Name", "VHDX", "Status"], g.Wlb.Select(e => new[] { e.Id, e.Desc, e.Target, Flags(e) }));
        if (g.System.Count > 0)
        {
            ctx.Say("Non-VHD system boot entries (can only be set as default / next boot: entry-default, entry-next):");
            foreach (var e in g.System) ctx.Say($"  {e.Id}  {e.Desc}  {e.Target}  {Flags(e)}");
        }
        if (g.Other.Count > 0)
        {
            ctx.Say("Other VHD boot entries (not handled, listed only):", Level.Dim);
            foreach (var e in g.Other) ctx.Say($"  {e.Id}  {e.Desc}  {e.Target}  {e.FlagText}".TrimEnd(), Level.Dim);
        }
        return 0;

        static string Flags(Bcd.Entry e) => e.Flags() is { Count: > 0 } f ? string.Join(", ", f) : "In the menu";
    }
}
