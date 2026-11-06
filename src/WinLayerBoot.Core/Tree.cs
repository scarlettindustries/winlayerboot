using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinLayerBoot.Core;

/// <summary>
/// Inheritance: the disks in the vhd directory are arranged as a tree by parent relation, and each disk carries its WLB boot entries.
/// Reads the VHDX file header to get the parent and the linkage GUID, without mounting; marks parents that were written to after the child disk was created (parent_linkage does not match).
/// </summary>
public sealed class Tree
{
    public sealed class Node
    {
        public Context.Disk Disk = null!;
        public Node? Parent;
        public List<Node> Children = [];
        public List<Bcd.Entry> Entries = [];
        /// <summary>Parent linkage: OK / parent was written to / parent missing / base disk (no parent).</summary>
        public string Link = "";
        public bool Broken;
        public int Depth => Parent is null ? 0 : Parent.Depth + 1;

        /// <summary>Disk tags: base disk / parent disk / child disk, read-only, mounted, has unreplayed log, size (shared by the window cards and the text tree).</summary>
        public List<string> Tags()
        {
            var d = Disk;
            List<string> tags = [d.Info is { HasParent: false } ? "Base disk" : Children.Count > 0 ? "Parent disk" : "Child disk"];
            if (d.ReadOnly) tags.Add("Read-only");
            if (d.Attached) tags.Add("Mounted");
            if (d.Info is { LogGuid: var lg } && lg != Guid.Empty) tags.Add("Unreplayed log");
            tags.Add($"{Files.GB1(d.FileSize)} GB");
            if (d.Info is not null) tags.Add($"Max {Math.Round(d.Info.VirtualSize / (double)(1L << 30))} GB");
            return tags;
        }
    }

    public List<Node> Roots = [];
    public List<Node> All = [];
    /// <summary>WLB boot entries that point outside the vhd directory, or whose file no longer exists.</summary>
    public List<Bcd.Entry> Orphans = [];

    public static Tree Build(Context ctx)
    {
        var t = new Tree();
        var disks = ctx.Disks();
        var nodes = disks.ToDictionary(d => d.Path, d => new Node { Disk = d }, StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes.Values)
        {
            var info = n.Disk.Info;
            if (info is null) { n.Link = "Cannot read file header"; n.Broken = true; continue; }
            if (!info.HasParent) { n.Link = "Base disk"; continue; }
            if (info.ParentPath == "" || !File.Exists(info.ParentPath)) { n.Link = $"Parent disk missing: {(info.ParentPath == "" ? info.ParentRelative : info.ParentPath)}"; n.Broken = true; continue; }
            if (!nodes.TryGetValue(info.ParentPath, out var p))
            {
                // Parent in another directory: native boot cannot find it, and it is not drawn in the tree
                n.Link = $"Parent disk is in another folder: {info.ParentPath}";
                n.Broken = true;
                continue;
            }
            n.Parent = p;
            p.Children.Add(n);
            var pinfo = p.Disk.Info;
            if (pinfo is not null && info.ParentLinkage != Guid.Empty && pinfo.DataWriteGuid != info.ParentLinkage)
            {
                n.Link = "The parent disk was written to after this disk was created; this disk is corrupted";
                n.Broken = true;
            }
            else n.Link = "OK";
        }
        foreach (var n in nodes.Values) n.Children = n.Children.OrderBy(c => c.Disk.Mtime).ToList();
        t.Roots = nodes.Values.Where(n => n.Parent is null).OrderByDescending(n => n.Disk.IsBase).ThenBy(n => n.Disk.Mtime).ToList();
        void Walk(Node n) { t.All.Add(n); foreach (var c in n.Children) Walk(c); }
        foreach (var r in t.Roots) Walk(r);

        foreach (var e in ctx.Bcd.WlbEntries())
        {
            if (e.Vhd != "" && nodes.TryGetValue(e.Vhd, out var n)) n.Entries.Add(e);
            else t.Orphans.Add(e);
        }
        return t;
    }

    /// <summary>Text form (used by CLI and TUI).</summary>
    public List<string> Render(bool verbose = true)
    {
        List<string> lines = [];
        void Walk(Node n, string indent, bool last, bool root)
        {
            var d = n.Disk;
            var branch = root ? "" : indent + (last ? "└─ " : "├─ ");
            lines.Add($"{branch}{d.Name}.vhdx  [{string.Join(", ", n.Tags())}]{(n.Broken ? "  ⚠ " + n.Link : "")}");
            var sub = root ? "" : indent + (last ? "   " : "│  ");
            if (verbose)
            {
                if (n.Entries.Count == 0) lines.Add($"{sub}{(n.Children.Count > 0 ? "│  " : "   ")}Boot entry: none");
                foreach (var e in n.Entries)
                    lines.Add($"{sub}{(n.Children.Count > 0 ? "│  " : "   ")}Boot entry: {e.Id}  {e.Desc}  {e.FlagText}".TrimEnd());
            }
            for (var i = 0; i < n.Children.Count; i++) Walk(n.Children[i], sub, i == n.Children.Count - 1, false);
        }
        for (var i = 0; i < Roots.Count; i++)
        {
            Walk(Roots[i], "", true, true);
            if (i < Roots.Count - 1) lines.Add("");
        }
        if (Orphans.Count > 0)
        {
            lines.Add("");
            lines.Add("WLB boot entries pointing to missing files or files outside the folder:");
            foreach (var e in Orphans) lines.Add($"  {e.Id}  {e.Desc}  → {e.Target}  {e.FlagText}".TrimEnd());
        }
        return lines;
    }
}
