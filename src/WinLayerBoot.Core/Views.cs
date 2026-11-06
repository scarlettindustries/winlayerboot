using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinLayerBoot.Core;

// Display data and text shared by the four frontends: overview, boot entry groups, buttons on the boot entries page, and the UI's background queries.

/// <summary>Data for the overview page: inheritance tree + environment status. A read runs bcdedit and reads all VHDX headers, so the UI reads it on a background thread.</summary>
public sealed record OverviewData(Tree Tree, string VhdDir, long FreeGB, bool IsAdmin, bool OnWorkHost)
{
    public static OverviewData Load(Context ctx) =>
        new(Tree.Build(ctx), ctx.Cfg.VhdDir, Files.FreeGB(ctx.Cfg.VhdDir), Files.IsAdmin(), ctx.OnWorkHost());

    public string Env => $"{VhdDir}  ·  {FreeGB} GB free  ·  {(IsAdmin ? "Administrator" : "Not administrator (runs will fail)")}  ·  {(OnWorkHost ? "On the work host" : "Not on the work host")}  ·  only boot entries starting with {Config.EntryPrefix} are listed";

    public string Totals => $"{Tree.All.Count} disks ({Tree.All.Count(x => x.Disk.IsBase)} base disks), {Tree.All.Sum(x => x.Entries.Count)} WLB boot entries, {Tree.All.Count(x => x.Broken)} with problems";
}

/// <summary>Boot entries fall into three groups by what they can do: WLB full-featured, non-VHD system boot entries (can only be set as default / next boot), other VHD boot entries (listed only).</summary>
public sealed record EntryGroups(List<Bcd.Entry> Wlb, List<Bcd.Entry> System, List<Bcd.Entry> Other)
{
    public static EntryGroups Load(Context ctx)
    {
        var all = ctx.Bcd.Entries();
        return new(all.Where(e => e.IsWlb).ToList(), all.Where(e => e.Tier == "system").ToList(), all.Where(e => e.Tier == "other").ToList());
    }
}

/// <summary>What can be done with a disk in the overview: jump to the matching operation, prefilled with this disk.</summary>
public sealed record DiskAction(string Text, string Op, Values Prefill)
{
    public static List<DiskAction> For(Tree.Node n)
    {
        var name = n.Disk.Name;
        if (n.Broken) return [];
        if (n.Disk.IsBase) return [new("Create child disk from this", "layer", new Values { ["Base"] = name })];
        List<DiskAction> l = [new("Take snapshot", "snapshot", new Values { ["Parent"] = name })];
        if (n.Entries.FirstOrDefault() is { } e) l.Add(new("Export as standalone disk", "export", new Values { ["Entry"] = e.Desc }));
        else l.Add(new("Add boot entry", "entry-add", new Values { ["Vhd"] = name }));
        l.Add(new("Save display settings", "display", new Values { ["Source"] = name }));
        return l;
    }
}

/// <summary>How an operation on the boot entries page proceeds: jump to a form page, pop up a dialog for a new name, pop up a confirmation dialog, or do it directly on click.</summary>
public enum EntryActionKind { Page, Rename, Confirm, Instant }

/// <summary>Operations on the boot entries page. When the selected boot entry does not qualify, the button is greyed out (Applies).</summary>
public sealed record EntryAction(string Text, string Op, EntryActionKind Kind = EntryActionKind.Instant, bool NeedSel = true, bool WlbOnly = true)
{
    public static readonly EntryAction[] All =
    [
        new("Add from VHD", "entry-add", EntryActionKind.Page, NeedSel: false),
        new("Rename", "entry-rename", EntryActionKind.Rename),
        new("Delete", "entry-delete", EntryActionKind.Confirm),
        new("Hide", "entry-hide"),
        new("Show", "entry-show"),
        new("Set as next boot", "entry-next", WlbOnly: false),
        new("Set as default", "entry-default", WlbOnly: false),
    ];

    public bool Applies(Bcd.Entry? e) => !NeedSel || (e is not null && (!WlbOnly || e.IsWlb) && !Redundant(e));

    /// <summary>Already in this state, so clicking again is pointless.</summary>
    private bool Redundant(Bcd.Entry e) => Op switch
    {
        "entry-hide" => e.Hidden, "entry-show" => !e.Hidden, "entry-default" => e.IsDefault, _ => false,
    };

    public Values Prefill(Bcd.Entry? e) => NeedSel && e is not null ? new Values { ["Entry"] = e.Desc } : [];

    public string ConfirmText(Bcd.Entry e) => $"Delete '{e.Desc}' from the boot menu? The VHDX file is not touched; you can add it back later with 'Add from VHD'.";
}

/// <summary>
/// The UI's background query: runs on the thread pool (mounting disks, WMI, reading the offline registry never block the UI), results are sent back to the UI thread with post;
/// only one runs at a time and only the last request counts: if the value is changed several times in a row, after the running one finishes the latest runs directly, the ones in between are skipped, and the result of the earlier one is discarded (queries mount disks, so they must not overlap).
/// All methods and Busy are used only on the UI thread.
/// </summary>
public sealed class Latest(Action<Action> post)
{
    private Action? _next;

    public bool Busy { get; private set; }

    public void Run<T>(Func<T> work, Action<T> done, Action<Exception> fail)
    {
        void Start() => Task.Run(() =>
        {
            Action finish;
            try { var r = work(); finish = () => done(r); }
            catch (Exception e) { finish = () => fail(e); }
            post(() =>
            {
                if (_next is { } next) { _next = null; next(); return; }
                Busy = false;
                finish();
            });
        });
        if (Busy) { _next = Start; return; }
        Busy = true;
        Start();
    }
}
