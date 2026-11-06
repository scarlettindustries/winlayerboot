using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;

namespace WinLayerBoot.Customize;

/// <summary>Default user personalization: taskbar left-aligned, taskbar only on the primary display, show file extensions and hidden files, Bing suggestions.</summary>
public sealed class Personalize : Customization
{
    public override string Key => "Personalize";
    public override string Label => "Personalization";
    public override string Help => "Written to the default user: taskbar left-aligned, taskbar only on the primary display, show file extensions and hidden files, Microsoft Pinyin cloud candidates";

    public override void Apply(LayerRun r)
    {
        var def = r.DefUser();
        var adv = $@"{def}\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        Hives.Dword("TaskbarAl", adv, "TaskbarAl", 0);
        Hives.Dword("HideFileExt", adv, "HideFileExt", 0);
        Hives.Dword("Hidden", adv, "Hidden", 1);
        Hives.Dword("MMTaskbar", adv, "MMTaskbarEnabled", 0);   // Turn off "Show taskbar on all displays"
        Hives.Dword("CloudCand", $@"{def}\Software\Microsoft\InputMethod\Settings\CHS", "Enable Cloud Candidate", 1);
        r.Unload();
    }
}

/// <summary>Server: uninstalls the Windows-Defender feature. Client does not change Security Center and Defender.</summary>
public sealed class RemoveDefender : Customization
{
    public override string Key => "NoDefender";
    public override string Label => "Uninstall Defender";
    public override string Help => "Disable the Windows-Defender feature with DISM (service and drivers removed too); Server base disks only";
    public override bool Applies(LayerPlan p) => p.IsServer;
    public override string NotApplicable(LayerPlan p) => "Client does not change Security Center and Defender";

    public override void Apply(LayerRun r)
    {
        // Disabled offline the official way; must be done before the hive is loaded
        r.Dism(d => d.DisableFeature("Windows-Defender"));
    }
}

/// <summary>Disables automatic sleep and screen off (power policy). Server does not sleep by default.</summary>
public sealed class NoSleep : Customization
{
    public override string Key => "NoSleep";
    public override string Label => "Disable automatic sleep and screen off";
    public override string Help => "Power plan: sleep and display off both set to never (AC, battery)";
    public override bool Applies(LayerPlan p) => !p.IsServer;
    public override string NotApplicable(LayerPlan p) => "Server does not sleep by default";

    public override void Apply(LayerRun r)
    {
        var soft = r.Soft();
        foreach (var g in new[] { "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e" })
        {
            Hives.Dword($"power {g} AC", $@"{soft}\Policies\Microsoft\Power\PowerSettings\{g}", "ACSettingIndex", 0);
            Hives.Dword($"power {g} DC", $@"{soft}\Policies\Microsoft\Power\PowerSettings\{g}", "DCSettingIndex", 0);
        }
        r.Unload();
    }
}

/// <summary>Since 24H2, with TPM + Secure Boot the internal drive is encrypted automatically after OOBE; once the partition holding the VHDX files is encrypted, no child disk can boot natively.</summary>
public sealed class NoBitLocker : Customization
{
    public override string Key => "NoBitLocker";
    public override string Label => "Block BitLocker automatic device encryption";
    public override string Help => "SYSTEM\\...\\Control\\BitLocker\\PreventDeviceEncryption = 1";
    public override bool Applies(LayerPlan p) => !p.IsServer;
    public override string NotApplicable(LayerPlan p) => "Server has no automatic device encryption";

    public override void Apply(LayerRun r)
    {
        var sys = r.Sys();
        Hives.Dword("PreventDeviceEncryption", $@"{sys}\ControlSet001\Control\BitLocker", "PreventDeviceEncryption", 1);
        r.Unload();
    }
}

/// <summary>
/// In Win11's first-boot specialize pass, memory integrity (HVCI) and with it VBS are turned on automatically according to the hardware. A child disk that has been booted once on a real machine carries this,
/// and later booting in a VM resets the whole machine before drivers load. Writing Enabled=0 in advance opts out of the automatic enabling via the registry.
/// </summary>
public sealed class NoVbs : Customization
{
    public override string Key => "NoVbs";
    public override string Label => "Do not auto-enable VBS and memory integrity";
    public override string Help => "HVCI Enabled = 0, EnableVirtualizationBasedSecurity = 0; otherwise a child disk will not start in a VM after the physical machine has booted it";
    public override bool Applies(LayerPlan p) => !p.IsServer;
    public override string NotApplicable(LayerPlan p) => "Server does not enable it automatically";

    public override void Apply(LayerRun r)
    {
        var sys = r.Sys();
        Hives.Dword("HVCI off", $@"{sys}\ControlSet001\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0);
        Hives.Dword("VBS off", $@"{sys}\ControlSet001\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0);
        r.Unload();
    }
}

/// <summary>Serial console SAC: on client, add the on-demand feature from LOF; Server has it built in; sacsvr starts automatically. The boot entry needs EMS enabled (the child disk build flow handles that).</summary>
public sealed class Sac : Customization
{
    public override string Key => "Sac";
    public override string Label => "Serial console SAC";
    public override bool DefaultOn => false;
    public override string Help => "Client adds EMS-SAC.Tools from the LOF ISO, Server uses its built-in one; sacsvr starts automatically; EMS on in the boot entry";

    public override void Check(LayerPlan p, Values v, CheckList c)
    {
        if (p.IsServer) return;
        var lof = p.LofDir;
        c.Add("LOF folder", lof != "" && File.Exists(Path.Combine(lof, "FoDMetadata_Client.cab")), lof != "" ? $"{lof} (source of serial console and SSH server)" : $"LOF ISO not found (LofIso in Settings or {p.Cfg.IsoDir}\\*LOF*.iso)");
    }

    public override void Apply(LayerRun r)
    {
        if (r.Plan.IsServer) r.Say("    Server has built-in SAC, not added from LOF");
        else r.Dism(d => d.AddCapability("Windows.Desktop.EMS-SAC.Tools~~~~0.0.1.0", r.Plan.LofDir));
        if (!File.Exists($@"{r.Win}\Windows\System32\drivers\sacdrv.sys")) throw new WlbError("sacdrv.sys not found");
        var sys = r.Sys();
        Hives.Dword("sacsvr auto", $@"{sys}\ControlSet001\Services\sacsvr", "Start", 2);
        r.Unload();
    }
}

/// <summary>SSH server: sshd starts automatically, public key login, default PowerShell. Client adds it from LOF; Server has it built in.</summary>
public sealed class Ssh : Customization
{
    public override string Key => "Ssh";
    public override string Label => "SSH server";
    public override bool DefaultOn => false;
    public override string Help => "sshd starts automatically, the public key from Settings is written to administrators_authorized_keys, default shell changed to PowerShell";

    public override IEnumerable<Field> ExtraFields(LayerPlan p, Values v) =>
        [new Field("PubKey", "SSH public key", FieldKind.Text, p.Cfg.PubKey) { Group = Group, Help = "Written to administrators_authorized_keys; defaults to the one in Settings" }];

    public override void Check(LayerPlan p, Values v, CheckList c)
    {
        var key = v.Get("PubKey", p.Cfg.PubKey);
        c.Add("Public key format", Regex.IsMatch(key, @"^(ssh-(ed25519|rsa)|ecdsa-sha2-\S+) \S+"), key == "" ? "Not set. Fill it in here, or set a default in 'Settings'" : key.Substring(0, Math.Min(40, key.Length)));
        if (p.IsServer) return;
        var lof = p.LofDir;
        if (!Customization.All.OfType<Sac>().First().On(v))   // When SAC is not selected, the LOF check is provided here
            c.Add("LOF folder", lof != "" && File.Exists(Path.Combine(lof, "FoDMetadata_Client.cab")), lof != "" ? lof : "LOF ISO not found");
    }

    public override void Apply(LayerRun r)
    {
        if (r.Plan.IsServer) r.Say("    Server has built-in sshd, not added from LOF");
        else r.Dism(d => d.AddCapability("OpenSSH.Server~~~~0.0.1.0", r.Plan.LofDir));
        if (!File.Exists($@"{r.Win}\Windows\System32\OpenSSH\sshd.exe")) throw new WlbError("sshd.exe not found");
        var sys = r.Sys();
        var soft = r.Soft();
        Hives.Dword("sshd auto", $@"{sys}\ControlSet001\Services\sshd", "Start", 2);
        Hives.String("DefaultShell", $@"{soft}\OpenSSH", "DefaultShell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        r.Unload();
        Directory.CreateDirectory($@"{r.Win}\ProgramData\ssh");
        var keys = $@"{r.Win}\ProgramData\ssh\administrators_authorized_keys";
        Files.WriteText(keys, r.V.Get("PubKey", r.Plan.Cfg.PubKey), Encoding.ASCII);
        Files.RestrictToAdmins(keys);
        r.Say("    authorized_keys keeps only Administrators and SYSTEM permissions");
    }
}
