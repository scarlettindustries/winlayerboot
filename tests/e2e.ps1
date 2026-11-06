# End-to-end test (PowerShell 7, administrator, run on the work host): use the published wlb.exe to go through self-check → base disk → child disk (SSH on) → snapshot → export, verifying each item.
# Uses e2e-* disks and boot entries and deletes them at the end; {bootmgr} and {fwbootmgr} of the system BCD must be the same before and after. Exit code 1 if any item fails.
#   -Exe            the wlb.exe to test (default: the official publish directory)
#   -ReferenceBase  base disk made by dism.exe: if given, compare disk size and the contents and permissions of several key files, to verify WIMApplyImage
param(
    [string]$Exe = "$PSScriptRoot\..\src\WinLayerBoot.Cli\bin\x64\Release\net48\publish\wlb.exe",
    [string]$ReferenceBase = ""
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

Add-Type @'
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class VD {
  [StructLayout(LayoutKind.Sequential)] public struct T { public uint Id; public Guid V; }
  [DllImport("virtdisk.dll", CharSet=CharSet.Unicode)] static extern uint OpenVirtualDisk(ref T t, string p, uint a, uint f, IntPtr par, out SafeFileHandle h);
  [DllImport("virtdisk.dll")] static extern uint GetVirtualDiskInformation(SafeFileHandle h, ref uint s, IntPtr i, IntPtr u);
  // Returns virtual size (GiB), block size (KiB), logical sector, type (2 fixed, 3 dynamic, 4 differencing)
  public static long[] Info(string path) {
    var t = new T { Id = 3, V = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B") };
    var par = Marshal.AllocHGlobal(64); for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(par, i, 0); Marshal.WriteInt32(par, 0, 2); Marshal.WriteInt32(par, 4, 1); Marshal.WriteInt32(par, 8, 1);
    SafeFileHandle h; var r = OpenVirtualDisk(ref t, path, 0, 0, par, out h); if (r != 0) throw new Exception("open " + r);
    var b = Marshal.AllocHGlobal(1024);
    Marshal.WriteInt64(b, 0, 0); Marshal.WriteInt32(b, 0, 1); uint s = 1024; GetVirtualDiskInformation(h, ref s, b, IntPtr.Zero);
    long vs = Marshal.ReadInt64(b, 8) >> 30, block = Marshal.ReadInt32(b, 24) >> 10, sect = Marshal.ReadInt32(b, 28);
    Marshal.WriteInt64(b, 0, 0); Marshal.WriteInt32(b, 0, 7); s = 1024; GetVirtualDiskInformation(h, ref s, b, IntPtr.Zero);
    long sub = Marshal.ReadInt32(b, 8); h.Dispose();
    return new long[] { vs, block, sect, sub };
  }
}
'@

$script:fails = 0; $script:passes = 0
$t0 = Get-Date
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { $script:passes++ } else { $script:fails++ }
    Write-Host ("[{0,4:N0}s] {1,-4} {2}{3}" -f ((Get-Date) - $t0).TotalSeconds, $(if ($ok) { "OK" } else { "FAIL" }), $name, $(if ($detail) { ": $detail" } else { "" }))
}
# Run wlb, with the output kept in $script:last; returns whether it succeeded
function Wlb([string]$name, [string[]]$a) {
    $script:last = & $Exe @a 2>&1 | Out-String -Width 240
    $code = $LASTEXITCODE
    Check $name ($code -eq 0) "Exit code $code"
    if ($code -ne 0) { ($script:last -split "`n" | Select-Object -Last 20) | ForEach-Object { Write-Host "       $_" } }
    return $code -eq 0
}
function Bcd() { (bcdedit /enum "{bootmgr}" /v | Out-String) + (bcdedit /enum "{fwbootmgr}" /v | Out-String) }
# Read-only mount, returns the volume path of the Windows partition (\\?\Volume{...}\); confirms that it is indeed this disk that was mounted
function Mount-Win([string]$vhdx, [string]$access = "ReadOnly") {
    $disk = Mount-DiskImage -ImagePath $vhdx -Access $access -NoDriveLetter -PassThru -ErrorAction Stop | Get-DiskImage | Get-Disk
    if ($disk.Location -ne $vhdx) { throw "Attached disk is $($disk.Location), not $vhdx" }
    $part = Get-Partition -DiskNumber $disk.Number | Where-Object Type -eq Basic | Sort-Object Size -Descending | Select-Object -First 1
    $part.AccessPaths | Where-Object { $_ -like "\\?\Volume*" } | Select-Object -First 1
}
# Compare two volumes with robocopy: /L only lists without copying, /XJ skips junctions. The result is written to a file and counted line by line, not read into memory. Compares only size and timestamp, not content.
# The directory holding the hive changes once the registry has been read offline, so it is counted separately; a difference anywhere else counts as failure. An exit code below 8 means it ran normally.
function Robocopy-Check([string]$name, [string]$volA, [string]$volB) {
    $work = Join-Path $env:TEMP "wlb-e2e-rc-$PID"
    $a = "$work\a"; $b = "$work\b"; $log = "$work.log"
    New-Item -ItemType Directory -Force $a, $b | Out-Null
    mountvol $a $volA; mountvol $b $volB
    try {
        cmd /c "robocopy `"$a`" `"$b`" /L /MIR /XJ /FP /NJH /NJS /R:0 /W:0 > `"$log`""
        $code = $LASTEXITCODE
        $diff = 0; $hive = 0; $errors = 0; $first = [Collections.Generic.List[string]]::new()
        foreach ($l in [IO.File]::ReadLines($log)) {
            if ($l -cmatch 'ERROR \d+ \(0x') { $errors++; continue }
            # The status word is at the start of the line and case-sensitive: placeholder and Folders inside paths do not count; only lines starting with a digit are directory statistics lines
            if ($l -cnotmatch '^\s*(\*EXTRA (File|Dir)|New (File|Dir)|Newer|Older|Changed|Modified|Tweaked|Mismatch)\b') { continue }
            if ($l -match '\\Windows\\System32\\config\\') { $hive++; continue }
            $diff++
            if ($first.Count -lt 5) { $first.Add(($l.Replace($a, '').Replace($b, '') -replace '\s+', ' ').Trim()) }
        }
        Check "robocopy ran OK for $name" ($code -lt 8) "Exit code $code, $errors read errors"
        Check "robocopy compare of $name (size and timestamp only, not content)" ($diff -eq 0) "$diff differences, $hive more in the hive folder$(if ($first.Count) { '; first few: ' + ($first -join ' ; ') })"
    }
    finally {
        mountvol $a /D; mountvol $b /D
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
        Remove-Item -Force $log -ErrorAction SilentlyContinue
    }
}
function File-Sig([string]$path) {
    $s = [IO.File]::OpenRead($path)
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($s)) } finally { $s.Dispose() }
    "$hash|" + [IO.FileSystemAclExtensions]::GetAccessControl([IO.FileInfo]::new($path)).GetSecurityDescriptorSddlForm("All")
}

$vhd = "V:\WLB\vhd"
$base = "$vhd\WLB-stage1-e2e-base.vhdx"; $child = "$vhd\e2e-child.vhdx"; $snap = "$vhd\e2e-snap.vhdx"; $spaced = "$vhd\e2e snap2.vhdx"; $export = "V:\WLB\export\e2e-export.vhdx"
$dispIn = "V:\WLB\display\e2e-restore"; $dispOut = "V:\WLB\display\e2e-saved"
"Under test: $Exe  $(& $Exe version)"
$bcdBefore = Bcd
try {
    Wlb "Self-test (WMI and the APIs, inside this exe)" @("selftest") | Out-Null
    ($script:last -split "`n" | Where-Object { $_ -match "OK  |Failed" }) | ForEach-Object { "       $($_.Trim())" }

    if (-not (Wlb "Create base disk" @("base", "-y", "--Name", "e2e-base"))) { throw "Base disk failed, cannot test the rest" }
    Check "Base disk shows unpacking progress" ($script:last -match "Unpacking image 100%")
    $i = [VD]::Info($base)
    Check "Base disk VHDX parameters" ($i[0] -eq 64 -and $i[1] -eq 32768 -and $i[2] -eq 512 -and $i[3] -eq 3) "$($i -join ', ')"
    Check "Base disk read-only" (Get-Item $base).IsReadOnly

    if ($ReferenceBase) {
        $len = (Get-Item $base).Length; $refLen = (Get-Item $ReferenceBase).Length
        Check "Base disk size is close to the dism one (hard links not expanded)" ([math]::Abs($len - $refLen) -lt 0.02 * $refLen) "$([math]::Round($len / 1GB, 2)) GB / $([math]::Round($refLen / 1GB, 2)) GB"
        $va = Mount-Win $base; $vb = Mount-Win $ReferenceBase
        try {
            Robocopy-Check "New base disk vs dism base disk" $va $vb
            foreach ($f in "Windows\System32\ntoskrnl.exe", "Windows\System32\kernel32.dll", "Windows\explorer.exe", "Windows\Boot\EFI\bootmgfw.efi") {
                Check "Content and permissions match: $f" ((File-Sig "$va$f") -eq (File-Sig "$vb$f"))
            }
        }
        finally { Dismount-DiskImage -ImagePath $base | Out-Null; Dismount-DiskImage -ImagePath $ReferenceBase | Out-Null }
    }

    New-Item -ItemType Directory -Force $dispIn | Out-Null
    reg.exe export HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration "$dispIn\display.reg" /y | Out-Null
    if (-not (Wlb "Create child disk (SSH on)" @("layer", "-y", "--Base", "WLB-stage1-e2e-base", "--Child", "e2e-child", "--Desc", "WLB-e2e-child", "--Ssh", "Y", "--Virtio", "Y", "--Display", "Y", "--DisplayName", "e2e-restore", "--BootNext", "N"))) { throw "Child disk failed, cannot test the rest" }
    Check "Child disk is a differencing disk" ((([VD]::Info($child))[3]) -eq 4)
    $v = Mount-Win $child
    try {
        $sddl = [IO.FileSystemAclExtensions]::GetAccessControl([IO.FileInfo]::new("${v}ProgramData\ssh\administrators_authorized_keys")).GetSecurityDescriptorSddlForm("Access")
        Check "authorized_keys permissions" ($sddl -eq "D:PAI(A;;FA;;;SY)(A;;FA;;;BA)") $sddl
        Check "virtio drivers are in the driver store" ([IO.Directory]::GetDirectories("${v}Windows\System32\DriverStore\FileRepository", "netkvm.inf_*").Count -gt 0)
        $presetFile = Join-Path $env:TEMP "wlb-e2e-preset-$PID.json"
        $presetNames = [IO.Directory]::GetFiles($v, "wlb-layer-*.json")
        Check "Preset file name in the child disk root has a git hash" ($presetNames.Count -eq 1 -and $presetNames[0] -match "wlb-layer-[0-9a-f]{7}\.json$") ($presetNames -join ", ")
        [IO.File]::Copy($presetNames[0], $presetFile, $true)
        $tmp = Join-Path $env:TEMP "wlb-e2e-SYSTEM-$PID"
        [IO.File]::Copy("${v}Windows\System32\config\SYSTEM", $tmp, $true)
    }
    finally { Dismount-DiskImage -ImagePath $child | Out-Null }
    reg.exe load HKLM\WLB_E2E $tmp | Out-Null
    $start = (Get-ItemProperty HKLM:\WLB_E2E\ControlSet001\Services\sshd).Start
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    reg.exe unload HKLM\WLB_E2E | Out-Null
    Remove-Item "$tmp*" -Force -ErrorAction SilentlyContinue
    Check "Offline registry written (sshd starts automatically)" ($start -eq 2) "Start=$start"
    $presetJson = Get-Content -Raw -Encoding UTF8 $presetFile | ConvertFrom-Json
    Check "Preset file recorded the settings of this run" ($presetJson.op -eq "layer" -and $presetJson.values.Child -eq "e2e-child" -and $presetJson.values.Ssh -eq "Y" -and $presetJson.values.Base -eq "WLB-stage1-e2e-base")
    Check "Preset file has no password" (-not ($presetJson.values.PSObject.Properties.Name -contains "Pass"))
    Wlb "Preset file can be imported" @("layer", "--load", $presetFile, "--Child", "e2e-reload", "--Desc", "WLB-e2e-reload", "--check") | Out-Null
    Wlb "Import configuration from child disk" @("layer", "--from-vhd", "e2e-child", "--check") | Out-Null
    Remove-Item -LiteralPath $presetFile -Force
    $locale = (bcdedit /enum osloader | Out-String) -split "(\r?\n){2,}" | Where-Object { $_ -match "WLB-e2e-child" } | ForEach-Object { [regex]::Match($_, "(?m)^locale\s+(\S+)").Groups[1].Value }
    $wantLocale = ((& $Exe config | Select-String "BootLocale") -replace "\s+", " ").Split(" ")[1]
    Check "New boot entry language comes from settings" ($locale -eq $wantLocale) "$locale"

    # The child disk has never been booted and has no user: build one from the default user's NTUSER so that saving the monitor settings can be exercised (what is restored into the child disk and saved out again should match the original)
    $v = Mount-Win $child "ReadWrite"
    try {
        [IO.Directory]::CreateDirectory("${v}Users\e2e") | Out-Null
        [IO.File]::Copy("${v}Users\Default\NTUSER.DAT", "${v}Users\e2e\NTUSER.DAT")
    }
    finally { Dismount-DiskImage -ImagePath $child | Out-Null }
    if (Wlb "Save display settings" @("display", "-y", "--Source", "e2e-child", "--User", "e2e", "--Name", "e2e-saved")) {
        # The image ships with an empty Connectivity key, which is included on save
        $saved = [IO.File]::ReadAllText("$dispOut\display.reg", [Text.Encoding]::Unicode).Replace("[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Connectivity]`r`n`r`n", "").Trim()
        Check "Restore then save, display.reg is the same as before" ($saved -eq [IO.File]::ReadAllText("$dispIn\display.reg", [Text.Encoding]::Unicode).Trim()) "$($saved.Length) characters"
    }

    if (-not (Wlb "Take snapshot" @("snapshot", "-y", "--Parent", "e2e-child", "--Child", "e2e-snap", "--Desc", "WLB-e2e-snap", "--HideParent", "N", "--BootNext", "N"))) { throw "Snapshot failed, cannot test the rest" }
    Check "Snapshot is a differencing disk" ((([VD]::Info($snap))[3]) -eq 4)

    # Boot entry management (the program reads and writes through WMI): cross-check independently with bcdedit
    function Entry-Block([string]$desc) { (bcdedit /enum osloader /v | Out-String) -split "(\r?\n){2,}" | Where-Object { $_ -match "(?m)^description\s+$([regex]::Escape($desc))\s*$" } }
    function Bm-Value([string]$key) { [regex]::Match((bcdedit /enum "{bootmgr}" /v | Out-String), "(?m)^$key\s+(\S+)").Groups[1].Value }
    function Menu-Text { [regex]::Match((bcdedit /enum "{bootmgr}" /v | Out-String), "(?ms)^displayorder\s+(.*?)^\S").Groups[1].Value }
    $snapBlock = Entry-Block "WLB-e2e-snap"
    $snapRel = [regex]::Escape($snap.Substring(2))
    Check "Snapshot device points to the new disk" ($snapBlock -match "(?m)^device\s+vhd=\[$($snap[0]):\]$snapRel,locate=custom:12000002") ([regex]::Match($snapBlock, "(?m)^device.*").Value)
    Check "Snapshot osdevice points to the new disk" ($snapBlock -match "(?m)^osdevice\s+vhd=\[$($snap[0]):\]$snapRel,locate=custom:22000002")
    Check "Snapshot is in the boot menu and has no hibernate resume entry" (((Menu-Text) -match [regex]::Match($snapBlock, "(?m)^identifier\s+(\{.*?\})").Groups[1].Value.Trim("{}")) -and ($snapBlock -notmatch "(?m)^resumeobject"))
    $cid = [regex]::Match((Entry-Block "WLB-e2e-child"), "(?m)^identifier\s+(\{.*?\})").Groups[1].Value
    $defaultBefore = Bm-Value "default"
    if (Wlb "Set as default" @("entry-default", "-y", "--Entry", "WLB-e2e-child")) { Check "Default entry is the child disk" ((Bm-Value "default") -eq $cid) }
    if (Wlb "Set as next boot" @("entry-next", "-y", "--Entry", "WLB-e2e-child")) { Check "Next boot is the child disk" ((Bm-Value "bootsequence") -eq $cid) }
    if (Wlb "Hide (it is both default and next boot)" @("entry-hide", "-y", "--Entry", "WLB-e2e-child")) {
        Check "Not in the menu after hiding" ((Menu-Text) -notmatch $cid.Trim("{}"))
        Check "Default reverts to the current system after hiding" ((Bm-Value "default") -eq $defaultBefore) "$(Bm-Value 'default')"
        Check "No next boot after hiding" ((Bm-Value "bootsequence") -eq "")
    }
    if (Wlb "Show" @("entry-show", "-y", "--Entry", "WLB-e2e-child")) { Check "Back in the menu after showing" ((Menu-Text) -match $cid.Trim("{}")) }
    if (Wlb "Rename" @("entry-rename", "-y", "--Entry", "WLB-e2e-child", "--Desc", "WLB-e2e-child-renamed")) {
        Check "Rename took effect" ([bool](Entry-Block "WLB-e2e-child-renamed"))
        Wlb "Rename back" @("entry-rename", "-y", "--Entry", "WLB-e2e-child-renamed", "--Desc", "WLB-e2e-child") | Out-Null
    }

    $sid = [regex]::Match((Entry-Block "WLB-e2e-snap"), "(?m)^identifier\s+(\{.*?\})").Groups[1].Value
    bcdedit /set $sid ems on | Out-Null   # The exported disk's BCD should follow the boot entry
    if (-not (Wlb "Export (add 2 GB)" @("export", "-y", "--Entry", "WLB-e2e-snap", "--Name", "e2e-export", "--ExpandGB", "2", "--Virtio", "N"))) { throw "Export failed, cannot test the rest" }
    Check "Export merging shows progress" ($script:last -match "Merging 100%")
    $i = [VD]::Info($export)
    Check "Exported disk is a standalone dynamic disk and was expanded" ($i[3] -eq 3 -and $i[0] -eq 66) "$($i -join ', ')"
    $va = Mount-Win $export; $vb = Mount-Win $snap
    try {
        Robocopy-Check "Exported disk vs differencing chain" $va $vb
        Check "Content matches: unattend.xml (written for the child disk)" ((File-Sig "${va}Windows\Panther\unattend.xml") -eq (File-Sig "${vb}Windows\Panther\unattend.xml"))
        Check "Content matches: ntoskrnl.exe (from the base disk)" ((File-Sig "${va}Windows\System32\ntoskrnl.exe") -eq (File-Sig "${vb}Windows\System32\ntoskrnl.exe"))
        Check "Unattend file switches back to the boot menu language at first logon" ([regex]::Matches([IO.File]::ReadAllText("${va}Windows\Panther\unattend.xml"), "bcdedit /set \{\w+\} locale $wantLocale").Count -eq 2) $wantLocale
    }
    finally { Dismount-DiskImage -ImagePath $export | Out-Null; Dismount-DiskImage -ImagePath $snap | Out-Null }
    $exportParts = Mount-DiskImage -ImagePath $export -Access ReadOnly -NoDriveLetter -PassThru | Get-DiskImage | Get-Disk | Get-Partition
    $parts = $exportParts | ForEach-Object { "$($_.Type) $([math]::Round($_.Size / 1MB))" }
    $espVol = ($exportParts | Where-Object Type -eq "System").AccessPaths | Where-Object { $_ -like "\\?\Volume*" }
    $espBcd = Join-Path $env:TEMP "wlb-e2e-esp.bcd"
    [IO.File]::Copy("${espVol}EFI\Microsoft\Boot\BCD", $espBcd, $true)
    Dismount-DiskImage -ImagePath $export | Out-Null
    $espText = bcdedit /store $espBcd /enum all /v | Out-String
    Remove-Item -LiteralPath $espBcd -Force
    $emsWant = "(?m)^bootems\s+Yes", "(?m)^ems\s+Yes", "(?m)^emsport\s+1\s*$", "(?m)^emsbaudrate\s+115200\s*$"
    Check "Original boot entry has EMS on, exported disk BCD has it on too" (@($emsWant | Where-Object { $espText -notmatch $_ }).Count -eq 0) (($espText -split "`r?`n" | Where-Object { $_ -match "^(boot)?ems" } | ForEach-Object { $_ -replace "\s+", "=" }) -join " ")
    Check "Exported disk partitions: ESP, MSR, Windows partition expanded" ($parts.Count -eq 3 -and $parts[0] -like "System*" -and [int]($parts[2].Split(" ")[1]) -gt 66000) ($parts -join " → ")

    Wlb "Snapshot with a space in its name" @("snapshot", "-y", "--Parent", "e2e-snap", "--Child", "e2e snap2", "--Desc", "WLB-e2e-snap2", "--HideParent", "N", "--BootNext", "N") | Out-Null
    Check "Disk with a space exists" (Test-Path -LiteralPath $spaced)

    $tree = & $Exe tree 2>&1 | Out-String -Width 240
    Check "Inheritance tree has no problems" ($tree -match ", 0 with problems" -and $tree -match "e2e snap2\.vhdx")
}
catch { Check "Test aborted" $false "$_" }
finally {
    "--- Cleanup"
    foreach ($e in "WLB-e2e-snap2", "WLB-e2e-snap", "WLB-e2e-child") { & $Exe entry-delete -y --Entry $e *> $null }
    foreach ($f in $spaced, $snap, $child, $base, $export) {
        if (Test-Path -LiteralPath $f) { Dismount-DiskImage -ImagePath $f -ErrorAction SilentlyContinue | Out-Null; Remove-Item -LiteralPath $f -Force }
    }
}
foreach ($d in $dispIn, $dispOut) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
Check "All e2e disks deleted" (@(Get-ChildItem $vhd, "V:\WLB\export" -Filter "*e2e*").Count -eq 0)
Check "All e2e boot entries deleted" (-not ((bcdedit /enum osloader | Out-String) -match "WLB-e2e"))
Check "System BCD {bootmgr} and {fwbootmgr} are the same as at the start" ((Bcd) -eq $bcdBefore)
$hives = @(reg.exe query HKLM | Select-String "WLB_")
Check "No leftover hives" ($hives.Count -eq 0) ($hives -join ", ")
$left = @(Get-Disk | Where-Object { $_.BusType -eq "File Backed Virtual" -and $_.Location -match "e2e" })
Check "No leftover mounts" ($left.Count -eq 0)
""
"Passed $script:passes, failed $script:fails, took $([int]((Get-Date) - $t0).TotalSeconds) seconds"
exit $(if ($script:fails -eq 0) { 0 } else { 1 })
