using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinLayerBoot.Core;

namespace WinLayerBoot.Customize;

/// <summary>Environment when building a child disk: base disk info, ISO directories (mounted on demand).</summary>
public sealed class LayerPlan(Context ctx, string baseVhdx, Context.Intl? intl)
{
    public Context Ctx { get; } = ctx;
    public Config Cfg => Ctx.Cfg;
    public string BaseVhdx { get; } = baseVhdx;
    public Context.Intl? Intl { get; } = intl;
    public bool IsServer => Intl?.IsServer == true;
    /// <summary>Directory name in the virtio ISO for the corresponding OS.</summary>
    public string VioOs => IsServer ? "2k25" : "w11";

    private string? _lof, _vio;

    /// <summary>LanguagesAndOptionalFeatures directory of the mounted LOF ISO; empty if the ISO does not exist.</summary>
    public string LofDir
    {
        get
        {
            if (_lof is not null) return _lof;
            var iso = Cfg.ResolveLofIso();
            _lof = iso != "" && File.Exists(iso) ? $@"{Ctx.Mnt.MountIso(iso, "iso-lof")}\LanguagesAndOptionalFeatures" : "";
            return _lof;
        }
    }

    /// <summary>Root directory of the mounted virtio ISO; empty if the ISO does not exist.</summary>
    public string VirtioDir
    {
        get
        {
            if (_vio is not null) return _vio;
            var iso = Cfg.ResolveVirtioIso();
            _vio = iso != "" && File.Exists(iso) ? Ctx.Mnt.MountIso(iso, "iso-virtio") : "";
            return _vio;
        }
    }
}

/// <summary>Child disk being built: the folder its Windows partition is mounted to.</summary>
public sealed class LayerRun(LayerPlan plan, Values v, string win)
{
    public LayerPlan Plan { get; } = plan;
    public Context Ctx => Plan.Ctx;
    public Values V { get; } = v;
    /// <summary>Folder the child disk's Windows partition is mounted to (no trailing backslash).</summary>
    public string Win { get; } = win;

    public void Say(string s, Level level = Level.Info) => Ctx.Say(s, level);
    public string Sys() => Ctx.Hives.Load("Sys", $@"{Win}\Windows\System32\config\SYSTEM");
    public string Soft() => Ctx.Hives.Load("Soft", $@"{Win}\Windows\System32\config\SOFTWARE");
    public string DefUser() => Ctx.Hives.Load("DefUser", $@"{Win}\Users\Default\NTUSER.DAT");
    public void Unload() => Ctx.Hives.UnloadAll();
    public void Dism(Action<DismImage> work)
    {
        using var dism = new DismImage(Win, Ctx.Out);
        work(dism);
    }
}

/// <summary>
/// One offline customization. Each declares its own preset items, checks, unattend file fragment and execution steps; the child disk build calls them one by one in order.
/// </summary>
public abstract class Customization
{
    public abstract string Key { get; }
    public abstract string Label { get; }
    public virtual string Help => "";
    public virtual bool DefaultOn => true;
    public virtual string Group => "Customization";
    /// <summary>Does this apply to the current base disk (client / Server)? If not, it is not listed and not executed.</summary>
    public virtual bool Applies(LayerPlan p) => true;
    /// <summary>Explanation given in the summary when it does not apply.</summary>
    public virtual string NotApplicable(LayerPlan p) => "";
    /// <summary>Extra preset items that appear only when this one is selected.</summary>
    public virtual IEnumerable<Field> ExtraFields(LayerPlan p, Values v) => [];
    public virtual void Check(LayerPlan p, Values v, CheckList c) { }
    /// <summary>Fragment for the unattend file's specialize pass.</summary>
    public virtual string UnattendSpecialize(LayerPlan p, Values v) => "";
    /// <summary>Name shown in the summary after completion.</summary>
    public virtual string SummaryName(Values v) => Label;
    /// <summary>Execute; called only when selected and applicable. Leave empty for those with no execution step (only write the unattend file).</summary>
    public virtual void Apply(LayerRun r) { }
    /// <summary>Has an execution step (takes one step in "offline customization").</summary>
    public virtual bool HasStep => true;

    public bool On(Values v) => v.Bool(Key, DefaultOn);

    /// <summary>All customizations, in execution order.</summary>
    public static readonly Customization[] All =
    [
        new Rdp(), new NoFirewall(), new EnglishInput(),
        new Personalize(), new RemoveDefender(), new NoSleep(), new Sac(), new Ssh(),
        new VirtioDrivers(), new MachineDrivers(), new NoBitLocker(), new DisplayRestore(), new NoVbs(),
    ];
}
