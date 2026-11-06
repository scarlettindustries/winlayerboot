using WinLayerBoot.Core;

namespace WinLayerBoot.Ops;

/// <summary>Preset items and steps shared by several operations.</summary>
internal static class Common
{
    /// <summary>The two switches "set as next boot" and "set as default boot entry" (create child disk, create snapshot, add boot entry from VHD). what is something like "this child disk" or "the new disk".</summary>
    public static Field[] BootFields(string what, bool nextOn = false, string group = "") =>
    [
        new Field("BootNext", "Set as next boot", FieldKind.Bool, nextOn ? "Y" : "N") { Group = group, Help = $"bootsequence: the next boot goes straight into {what}, once only" },
        new Field("BootDefault", "Set as default entry", FieldKind.Bool, "N") { Group = group, Help = $"bcdedit /default: after the menu times out, boots {what}, permanently" },
    ];

    /// <summary>Sets the next boot and the default boot entry according to the two switches, and reports the result.</summary>
    public static void ApplyBoot(Context ctx, string id, Values v, string what, bool nextOn = false)
    {
        if (v.Bool("BootNext", nextOn))
        {
            ctx.Bcd.BootNext(id);
            ctx.Say($"    bootsequence set: the next boot skips the menu and goes straight into {what}, once only.", Level.Warn);
        }
        else ctx.Say($"    Next boot not set. When needed use 'Boot entries → Next boot', or bcdedit /bootsequence {id}");
        if (v.Bool("BootDefault"))
        {
            ctx.Bcd.SetDefault(id);
            ctx.Say($"    Set as default boot entry: from now on the menu timeout boots {what}.", Level.Warn);
        }
        else ctx.Say("    Default boot entry unchanged. When needed use 'Boot entries → Set as default'");
    }
}
