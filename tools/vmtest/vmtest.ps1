<#
.SYNOPSIS
  Tests AltTab Everywhere in a Hyper-V virtual machine with 1 to 4 monitors, without touching
  your own screens.

.DESCRIPTION
  One script, nothing to install: it uses only Windows' own Hyper-V and PowerShell.

  First time ever: ./tools/vmtest/vmtest.ps1 -Setup
    Turns on Hyper-V and adds you to "Hyper-V Administrators" (one administrator prompt), then
    you restart Windows. Your WSL2 and Docker keep working: they already use the same hypervisor.

  Then: ./tools/vmtest/vmtest.ps1 [-Scenario selftest] [-Monitors 2]
    The first run builds the test VM, about 30 minutes, with one administrator prompt:
      1. downloads Microsoft's free Windows 11 Enterprise evaluation ISO (8.2 GB, kept for rebuilds);
      2. writes Windows straight onto a virtual disk, with an answer file that skips every setup screen;
      3. starts it, installs the open-source Virtual Display Driver for the extra monitors;
      4. saves a "ready" checkpoint and turns the VM off.
    Every run after that takes a minute or two:
      1. builds the app from this source;
      2. restores the "ready" checkpoint, copies the build in, sets the number of monitors;
      3. runs a scenario from tools/vmtest/scenarios in the VM's desktop session;
      4. copies its screenshots and logs back, turns the VM off, and exits with its result.

  Keys reach the VM through Hyper-V's virtual keyboard, so the app sees them as real key presses
  (it ignores simulated ones on purpose). The VM has no network and its clock doesn't follow this
  PC's, so the evaluation copy of Windows never sees time pass.

  Everything lives in .vmtest\ next to this repository (git ignores it): downloads, the VM, and
  results\<time>-<scenario>\. About 35 GB in all. -Remove deletes the VM.

.EXAMPLE
  ./tools/vmtest/vmtest.ps1 -Setup
.EXAMPLE
  ./tools/vmtest/vmtest.ps1
.EXAMPLE
  ./tools/vmtest/vmtest.ps1 -Scenario demo -Monitors 3
.EXAMPLE
  ./tools/vmtest/vmtest.ps1 -Remove
#>
[CmdletBinding()]
param(
    # A file in tools/vmtest/scenarios (without .ps1), or the path of your own scenario script.
    [string] $Scenario = "selftest",
    # Monitors in the VM: Hyper-V's own screen plus virtual ones.
    [ValidateRange(1, 4)] [int] $Monitors = 2,
    # Test this folder (the switcher's publish output) instead of building the app.
    [string] $AppDir,
    # Also build the settings window into the VM (self-contained, about 150 MB more to copy).
    [switch] $WithSettings,
    # Leave the VM running afterwards, to look at it in Hyper-V Manager.
    [switch] $KeepRunning,
    # Give up on a scenario after this many minutes.
    [int] $TimeoutMinutes = 15,
    # One time: turn on Hyper-V and add you to "Hyper-V Administrators" (administrator prompt, then restart).
    [switch] $Setup,
    # Delete the VM and build it again from the downloaded ISO.
    [switch] $Rebuild,
    # Delete the VM. Downloads are kept; delete .vmtest\downloads yourself to free them.
    [switch] $Remove,
    # Where the VM, downloads and results go.
    [string] $Root, # default: .vmtest next to the repository
    # VM size.
    [int] $MemoryMB = 4096,
    [int] $Processors = 2,
    [string] $Resolution = "1920x1080",
    # Internal: the administrator halves. Not for direct use.
    [string] $ElevatedDisk,
    [switch] $ElevatedSetup,
    [string] $UserSid,
    [string] $UserName
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$repo = Split-Path (Split-Path $PSScriptRoot)
# Not a parameter default: Windows PowerShell 5.1 (the administrator halves) has no $PSScriptRoot there.
if (-not $Root) { $Root = Join-Path $repo ".vmtest" }
$vmName = "AltTab Everywhere test"
$guestUser = "tester"
$hyperVAdmins = "S-1-5-32-578" # the "Hyper-V Administrators" group, in any language

# ---- Pinned downloads. Update the URL, size and SHA-256 together. ----
$iso = @{
    # Windows 11 Enterprise 26H2 evaluation, English (US), x64, from
    # https://www.microsoft.com/evalcenter/download-windows-11-enterprise
    Url    = "https://software-static.download.prss.microsoft.com/dbazure/26300.9457.260913-1737.26h2_ge_release_svc_refresh_CLIENTENTERPRISEEVAL_OEMRET_x64FRE_en-us.iso"
    Size   = 8225329152
    Sha256 = "" # Microsoft doesn't publish one: the first download prints it, to pin here.
}
$driver = @{
    # Virtual Display Driver 25.7.23 (MIT, signed by SignPath Foundation):
    # https://github.com/VirtualDrivers/Virtual-Display-Driver
    Url    = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip"
    Sha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a"
}

function Step($text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }
# The administrator halves always run in Windows PowerShell 5.1: Windows' image and feature
# cmdlets (DISM) fail with "Class not registered" in PowerShell 7.
function Invoke-Elevated([string[]] $arguments) {
    $windowsPowerShell = Join-Path $env:windir "System32\WindowsPowerShell\v1.0\powershell.exe"
    $p = Start-Process $windowsPowerShell -Verb RunAs -Wait -PassThru -ArgumentList (@(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"") + $arguments)
    return $p.ExitCode
}

# =====================================================================================
# Administrator half 1 (-Setup): turn on Hyper-V, add the user to Hyper-V Administrators.
# =====================================================================================
if ($ElevatedSetup) {
    try {
        Write-Host "Turning on Hyper-V..."
        & dism.exe /Online /Enable-Feature /FeatureName:Microsoft-Hyper-V-All /All /NoRestart
        # 3010: done, restart needed. 0x800f080c: no such feature (Windows Home has no Hyper-V).
        if ($LASTEXITCODE -eq -2146498548) { throw "This edition of Windows has no Hyper-V (it needs Pro, Enterprise or Education)." }
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) { throw "dism couldn't turn on Hyper-V (exit code $LASTEXITCODE)." }
        Write-Host "Adding you to Hyper-V Administrators..."
        $group = (New-Object Security.Principal.SecurityIdentifier $hyperVAdmins).Translate([Security.Principal.NTAccount]).Value.Split("\")[-1]
        try { ([ADSI]"WinNT://./$group,group").Add("WinNT://$UserSid") }
        catch {
            if ("$_" -notmatch "already a member|0x80070562") {
                & net localgroup "$group" "$UserName" /add 2>&1 | Out-Null # Entra ID accounts, if binding by SID failed
                if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 2) { throw "Couldn't add $UserName to $group." }
            }
        }
        exit 0
    }
    catch {
        Write-Host "Setup failed: $_ ($($_.InvocationInfo.PositionMessage))" -ForegroundColor Red
        Read-Host "Press Enter to close"
        exit 1
    }
}

# =====================================================================================
# Administrator half 2: write Windows onto the virtual disk. Needs administrator rights
# only to mount the disk and apply the image; everything else runs as you.
# =====================================================================================
if ($ElevatedDisk) {
    $job = Get-Content $ElevatedDisk -Raw | ConvertFrom-Json
    Start-Transcript -Path (Join-Path $job.Staging "disk.log") -Force | Out-Null
    $code = 0
    try {
        Step "Opening the Windows ISO"
        $isoImage = Mount-DiskImage -ImagePath $job.Iso -PassThru
        $isoDrive = ($isoImage | Get-Volume).DriveLetter + ":"
        $sig = Get-AuthenticodeSignature (Join-Path $isoDrive "setup.exe")
        if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
            throw "setup.exe on the ISO isn't signed by Microsoft ($($sig.Status)). Delete the ISO and run again."
        }
        $wim = Join-Path $isoDrive "sources\install.wim"
        if (-not (Test-Path $wim)) { $wim = Join-Path $isoDrive "sources\install.esd" }
        $image = Get-WindowsImage -ImagePath $wim | Where-Object ImageName -like "*Enterprise*" | Select-Object -First 1
        if (-not $image) { $image = Get-WindowsImage -ImagePath $wim -Index 1 }
        Step "Image: $($image.ImageName)"

        Step "Creating the virtual disk"
        New-VHD -Path $job.Vhd -SizeBytes 80GB -Dynamic | Out-Null
        $disk = Mount-VHD -Path $job.Vhd -Passthru -NoDriveLetter | Get-Disk
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Remove-Partition -Confirm:$false -ErrorAction SilentlyContinue
        $efi = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"
        Format-Volume -Partition $efi -FileSystem FAT32 -NewFileSystemLabel System -Confirm:$false | Out-Null
        New-Partition -DiskNumber $disk.Number -Size 16MB -GptType "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" | Out-Null
        $win = New-Partition -DiskNumber $disk.Number -UseMaximumSize -GptType "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}"
        Format-Volume -Partition $win -FileSystem NTFS -NewFileSystemLabel Windows -Confirm:$false | Out-Null
        $winPath = Join-Path $job.Staging "windows-volume"
        New-Item -ItemType Directory -Force $winPath | Out-Null
        Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $win.PartitionNumber -AccessPath $winPath
        Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber -AssignDriveLetter
        $efiDrive = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber).DriveLetter + ":"

        Step "Writing Windows to the disk (5 to 10 minutes)"
        Expand-WindowsImage -ImagePath $wim -Index $image.ImageIndex -ApplyPath "$winPath\" | Out-Null
        & bcdboot "$winPath\Windows" /s $efiDrive /f UEFI | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "bcdboot failed ($LASTEXITCODE)." }
        New-Item -ItemType Directory -Force "$winPath\Windows\Panther" | Out-Null
        Copy-Item (Join-Path $job.Staging "unattend.xml") "$winPath\Windows\Panther\unattend.xml"
        Copy-Item -Recurse (Join-Path $job.Staging "vmtest") "$winPath\vmtest"

        Remove-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber -AccessPath "$efiDrive\"
        Remove-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $win.PartitionNumber -AccessPath "$winPath\"
    }
    catch {
        Write-Host "Failed: $_" -ForegroundColor Red
        $code = 1
    }
    finally {
        Dismount-VHD -Path $job.Vhd -ErrorAction SilentlyContinue
        Dismount-DiskImage -ImagePath $job.Iso -ErrorAction SilentlyContinue | Out-Null
        Stop-Transcript | Out-Null
    }
    exit $code
}

# =====================================================================================
# Normal run, as you.
# =====================================================================================
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $me).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$hyperVOn = [bool](Get-Service vmms -ErrorAction SilentlyContinue)
$canManage = $isAdmin -or ($me.Groups.Value -contains $hyperVAdmins)

if ($Setup) {
    if ($hyperVOn -and $canManage) { Write-Host "Hyper-V is on and you can manage it: nothing to do."; exit 0 }
    Write-Host "Windows will ask for administrator rights to turn on Hyper-V and add you to Hyper-V Administrators."
    if ((Invoke-Elevated @("-ElevatedSetup", "-UserSid", $me.User.Value, "-UserName", "`"$($me.Name)`"")) -ne 0) { throw "The administrator step didn't finish." }
    Write-Host "Done. Restart Windows (that finishes turning on Hyper-V and the group membership), then run:" -ForegroundColor Green
    Write-Host "  ./tools/vmtest/vmtest.ps1"
    exit 0
}
if (-not $hyperVOn) { throw "Hyper-V is off. Turn it on once with: ./tools/vmtest/vmtest.ps1 -Setup  (administrator prompt, then restart Windows)" }
if (-not $canManage) {
    throw "You're not in Hyper-V Administrators yet. Run ./tools/vmtest/vmtest.ps1 -Setup, or if you just did, sign out and back in."
}
Import-Module Hyper-V

$vmDir = Join-Path $Root "vm"
$downloads = Join-Path $Root "downloads"
$credFile = Join-Path $vmDir "credential.xml"

function Remove-TestVM {
    $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
    if ($vm) {
        if ($vm.State -ne "Off") { Stop-VM -VM $vm -TurnOff -Force }
        Remove-VM -VM $vm -Force
    }
    if (Test-Path $vmDir) { Remove-Item -Recurse -Force $vmDir }
}

if ($Remove) { Remove-TestVM; Step "Removed the test VM."; exit 0 }

# ---- Talking to the VM (PowerShell Direct: no network needed) ----
function Get-GuestCredential { Import-Clixml $credFile }

function Wait-GuestSession([int] $minutes) {
    $deadline = (Get-Date).AddMinutes($minutes)
    $cred = Get-GuestCredential
    while ((Get-Date) -lt $deadline) {
        try {
            $s = New-PSSession -VMName $vmName -Credential $cred -ErrorAction Stop
            # Signed in on the VM's screen: Explorer runs in the desktop session.
            $ready = Invoke-Command -Session $s { [bool](Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -gt 0) }
            if ($ready) { return $s }
            Remove-PSSession $s
        }
        catch { }
        Start-Sleep -Seconds 5
    }
    throw "The VM didn't reach its desktop within $minutes minutes."
}

# Keys through Hyper-V's virtual keyboard: the guest sees real key presses, not injected ones.
function Send-VMKeys([string[]] $lines) {
    $system = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$vmName'"
    $keyboard = Get-CimAssociatedInstance -InputObject $system -ResultClassName Msvm_Keyboard
    foreach ($line in $lines) {
        $parts = $line.Trim() -split "\s+"
        if (-not $parts[0]) { continue }
        switch ($parts[0]) {
            "down" { Invoke-CimMethod -InputObject $keyboard -MethodName PressKey -Arguments @{ keyCode = [uint32] $parts[1] } | Out-Null }
            "up"   { Invoke-CimMethod -InputObject $keyboard -MethodName ReleaseKey -Arguments @{ keyCode = [uint32] $parts[1] } | Out-Null }
            "tap"  { Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32] $parts[1] } | Out-Null }
            "text" { Invoke-CimMethod -InputObject $keyboard -MethodName TypeText -Arguments @{ asciiText = ($parts[1..($parts.Count - 1)] -join " ") } | Out-Null }
            "wait" { Start-Sleep -Milliseconds ([int] $parts[1]) }
        }
    }
}

# The runner, copied into the VM on every run: it runs the scenario in the desktop session
# (a scheduled task as the signed-in user, not elevated) with these helpers.
$runner = @'
$ErrorActionPreference = "Stop"
$Out = "C:\vmtest\out"
$App = "C:\vmtest\app\AltTabEverywhere.exe"
$Settings = "C:\vmtest\app\AltTabEverywhere.Settings.exe"
$Params = Get-Content "C:\vmtest\params.json" -Raw | ConvertFrom-Json
$Monitors = [int] $Params.Monitors
$script:Failures = 0
Start-Transcript -Path "$Out\scenario.log" -Force | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class VmTestWin {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    public static string Title(IntPtr h) { var s = new System.Text.StringBuilder(512); GetWindowTextW(h, s, 512); return s.ToString(); }
    // The first visible top-level window whose title contains the text.
    public static IntPtr Find(string part) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { if (IsWindowVisible(h) && Title(h).Contains(part)) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
}
"@
[VmTestWin]::SetProcessDpiAwarenessContext([IntPtr] -4) | Out-Null # per-monitor v2: real pixels everywhere

function Step($text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }
function Fail($text) { $script:Failures++; Write-Host "FAIL: $text" }
function Get-Screens { [System.Windows.Forms.Screen]::AllScreens }
function Show-Screens { Get-Screens | ForEach-Object { Step "  screen $($_.DeviceName) $($_.Bounds) primary=$($_.Primary)" } }
# Waits until Windows shows $count monitors.
function Wait-Screens([int] $count = $Monitors, [int] $seconds = 30) {
    for ($i = 0; $i -lt $seconds * 2 -and (Get-Screens).Count -ne $count; $i++) { Start-Sleep -Milliseconds 500 }
    if ((Get-Screens).Count -ne $count) { Fail "expected $count monitors, Windows shows $((Get-Screens).Count)" }
    Show-Screens
}
# A PNG of every monitor together.
function Save-Screenshot([string] $name) {
    $v = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $v.Width, $v.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($v.Location, [System.Drawing.Point]::Empty, $v.Size)
    $bmp.Save("$Out\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Step "screenshot $name.png ($($v.Width)x$($v.Height))"
}
# Waits for a window whose title contains $title and puts it on monitor $screen (0-based), at
# $x,$y within it. Returns its handle.
function Move-TestWindow([string] $title, [int] $screen = 0, [int] $x = 80, [int] $y = 80, [int] $width = 800, [int] $height = 500) {
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200; $h = [VmTestWin]::Find($title) }
    if ($h -eq [IntPtr]::Zero) { Fail "no window titled '$title'"; return $h }
    $b = (Get-Screens)[$screen].WorkingArea
    [VmTestWin]::MoveWindow($h, $b.X + $x, $b.Y + $y, $width, $height, $true) | Out-Null
    return $h
}
# A plain window in a process of its own, titled $title, on monitor $screen. Returns its handle.
function New-TestWindow([string] $title, [int] $screen = 0, [int] $x = 80, [int] $y = 80, [int] $width = 800, [int] $height = 500) {
    $code = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = '$($title -replace "'", "''")'; [System.Windows.Forms.Application]::Run(`$f)"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
    Start-Process conhost.exe -ArgumentList "--headless", "powershell.exe", "-NoProfile", "-EncodedCommand", $encoded | Out-Null
    return Move-TestWindow $title $screen $x $y $width $height
}
# Starts a program and moves its window (found by title, since many programs hand their window to
# another process) to monitor $screen. Returns its handle.
function Start-TestWindow([string] $file, [string] $arguments, [string] $title, [int] $screen = 0, [int] $x = 80, [int] $y = 80, [int] $width = 800, [int] $height = 500) {
    if ($arguments) { Start-Process $file -ArgumentList $arguments } else { Start-Process $file }
    return Move-TestWindow $title $screen $x $y $width $height
}
# Real key presses, sent by the host through Hyper-V's virtual keyboard. Lines: "down 0x12",
# "up 0x12", "tap 0x09", "text abc", "wait 200". Virtual-key codes: Alt 0x12, Tab 0x09,
# Shift 0x10, Ctrl 0x11, Win 0x5B, Esc 0x1B, Enter 0x0D, arrows 0x25-0x28.
$script:keyRequest = 0
function Send-Keys([string[]] $lines, [int] $timeoutSeconds = 60) {
    $script:keyRequest++
    $file = "C:\vmtest\keys\$($script:keyRequest).req"
    Set-Content "$file.tmp" ($lines -join "`r`n")
    Move-Item "$file.tmp" $file
    for ($i = 0; $i -lt $timeoutSeconds * 10 -and -not (Test-Path "C:\vmtest\keys\$($script:keyRequest).done"); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path "C:\vmtest\keys\$($script:keyRequest).done")) { throw "The host didn't send keys within $timeoutSeconds s." }
}
function Press-AltTab([int] $tabs = 1, [int] $holdMs = 600) {
    $lines = @("down 0x12", "wait 50")
    for ($i = 0; $i -lt $tabs; $i++) { $lines += "tap 0x09", "wait 120" }
    $lines += "wait $holdMs", "up 0x12"
    Send-Keys $lines
}

$code = 0
try {
    Step "scenario $($Params.Scenario), $Monitors monitor(s)"
    . "C:\vmtest\scenario.ps1"
}
catch {
    Fail "$_ ($($_.InvocationInfo.PositionMessage))"
}
finally {
    if ($script:Failures -gt 0) { $code = 1 }
    Copy-Item "$env:LOCALAPPDATA\AltTabEverywhere\log*.txt" $Out -ErrorAction SilentlyContinue
    Step "result: $(if ($code -eq 0) { 'PASS' } else { "FAIL ($($script:Failures))" })"
    Stop-Transcript | Out-Null
    Set-Content "$Out\exitcode.txt" $code
}
'@

# Runs a scenario in the VM: copies the app, runner and scenario in, starts the task, sends
# keys when asked, and copies the results back. Returns the scenario's exit code.
function Invoke-Scenario($session, [string] $scenarioFile, [string] $app, [string] $resultDir, [int] $monitors) {
    $name = [IO.Path]::GetFileNameWithoutExtension($scenarioFile)
    Invoke-Command -Session $session {
        foreach ($d in "C:\vmtest\out", "C:\vmtest\keys", "C:\vmtest\app") {
            if (Test-Path $d) { Remove-Item -Recurse -Force $d }
            New-Item -ItemType Directory -Force $d | Out-Null
        }
    }
    if ($app) { Copy-Item -ToSession $session -Recurse (Join-Path $app "*") "C:\vmtest\app\" }
    Copy-Item -ToSession $session $scenarioFile "C:\vmtest\scenario.ps1"
    $tmp = Join-Path $env:TEMP "alttab-vmtest-runner.ps1"
    Set-Content $tmp $runner -Encoding UTF8
    Copy-Item -ToSession $session $tmp "C:\vmtest\runner.ps1"
    Remove-Item $tmp
    $params = @{ Scenario = $name; Monitors = $monitors } | ConvertTo-Json
    Invoke-Command -Session $session { param($p) Set-Content "C:\vmtest\params.json" $p } -ArgumentList $params

    Step "Running scenario '$name'"
    Invoke-Command -Session $session { Start-ScheduledTask -TaskName "vmtest" }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $done = $false
    while (-not $done -and (Get-Date) -lt $deadline) {
        $state = Invoke-Command -Session $session {
            $req = Get-ChildItem "C:\vmtest\keys\*.req" -ErrorAction SilentlyContinue | Sort-Object { [int] $_.BaseName } | Select-Object -First 1
            [pscustomobject] @{
                Done    = Test-Path "C:\vmtest\out\exitcode.txt"
                Request = if ($req) { $req.BaseName } else { $null }
                Lines   = if ($req) { @(Get-Content $req.FullName) } else { @() }
            }
        }
        if ($state.Request) {
            Send-VMKeys $state.Lines
            Invoke-Command -Session $session { param($n) Move-Item "C:\vmtest\keys\$n.req" "C:\vmtest\keys\$n.done" } -ArgumentList $state.Request
            continue
        }
        $done = $state.Done
        if (-not $done) { Start-Sleep -Milliseconds 250 }
    }
    New-Item -ItemType Directory -Force $resultDir | Out-Null
    Copy-Item -FromSession $session -Recurse "C:\vmtest\out\*" $resultDir -ErrorAction SilentlyContinue
    if (-not $done) { Step "The scenario didn't finish within $TimeoutMinutes minutes."; return 2 }
    return [int] (Get-Content (Join-Path $resultDir "exitcode.txt"))
}

# Sets the number of virtual monitors (the VM's own screen is one more). Runs as administrator
# in the VM through PowerShell Direct.
function Set-GuestMonitors($session, [int] $total) {
    Invoke-Command -Session $session {
        param($extra)
        $device = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains "Root\MttVDD" } | Select-Object -First 1
        if (-not $device) { throw "The virtual display driver isn't installed in the VM. Run with -Rebuild." }
        $file = "C:\VirtualDisplayDriver\vdd_settings.xml"
        $xml = Get-Content $file -Raw
        $current = [int] ([regex]::Match($xml, "<count>(\d+)</count>").Groups[1].Value)
        if ($extra -eq 0) {
            if ($device.Status -ne "Error") { Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false }
            return
        }
        if ($device.Status -eq "Error") { Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false }
        if ($current -ne $extra) {
            Set-Content $file ($xml -replace "<count>\d+</count>", "<count>$extra</count>") -Encoding UTF8
            & pnputil /restart-device "$($device.InstanceId)" | Out-Null
        }
    } -ArgumentList ($total - 1)
}

# ---- Downloads, checked against their pinned SHA-256 ----
function Get-Download([string] $url, [string] $file, [string] $sha256, [long] $size = 0) {
    if (-not (Test-Path $file) -or ($size -and (Get-Item $file).Length -ne $size)) {
        New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
        $mb = if ($size) { " ($([math]::Round($size / 1GB, 1)) GB)" } else { "" }
        Step "Downloading $(Split-Path $file -Leaf)$mb"
        # curl.exe ships with Windows; -C - resumes an interrupted download.
        & curl.exe -L --fail -C - -o $file $url
        if ($LASTEXITCODE -ne 0) { throw "Downloading $url failed (curl $LASTEXITCODE). Run again to resume." }
    }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $sha256) {
        Step "SHA-256 of $(Split-Path $file -Leaf): $actual (no pinned value in vmtest.ps1 yet)"
    }
    elseif ($actual -ne $sha256.ToLowerInvariant()) {
        Remove-Item $file
        throw "$(Split-Path $file -Leaf) doesn't match its pinned SHA-256, so it was deleted. Got $actual."
    }
}

# ---- Building the VM ----
function New-TestVM {
    Remove-TestVM
    $isoFile = Join-Path $downloads ([IO.Path]::GetFileName(([uri] $iso.Url).AbsolutePath))
    $driverZip = Join-Path $downloads ([IO.Path]::GetFileName(([uri] $driver.Url).AbsolutePath))
    Get-Download $iso.Url $isoFile $iso.Sha256 $iso.Size
    Get-Download $driver.Url $driverZip $driver.Sha256

    New-Item -ItemType Directory -Force $vmDir | Out-Null
    $staging = Join-Path $vmDir "staging"
    New-Item -ItemType Directory -Force "$staging\vmtest\driver" | Out-Null
    Expand-Archive $driverZip "$staging\driver-zip" -Force
    Get-ChildItem "$staging\driver-zip" -Recurse -File | Copy-Item -Destination "$staging\vmtest\driver"

    # A random password for the VM's only account, kept encrypted for you alone (Windows DPAPI).
    $chars = [char[]] "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    New-Object PSCredential $guestUser, (ConvertTo-SecureString $password -AsPlainText -Force) | Export-Clixml $credFile

    # The answer file: skips every setup screen, makes the account and signs it in automatically.
    $unattend = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>ALTTAB-TEST</ComputerName>
      <TimeZone>UTC</TimeZone>
    </component>
    <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Path>reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>en-US</InputLocale>
      <SystemLocale>en-US</SystemLocale>
      <UILanguage>en-US</UILanguage>
      <UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>$guestUser</Name>
            <Group>Administrators</Group>
            <Password><Value>$password</Value><PlainText>true</PlainText></Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <Username>$guestUser</Username>
        <Password><Value>$password</Value><PlainText>true</PlainText></Password>
        <LogonCount>999999</LogonCount>
      </AutoLogon>
    </component>
  </settings>
</unattend>
"@
    Set-Content (Join-Path $staging "unattend.xml") $unattend -Encoding UTF8

    $vhd = Join-Path $vmDir "windows.vhdx"
    $jobFile = Join-Path $staging "disk.json"
    @{ Iso = $isoFile; Vhd = $vhd; Staging = $staging } | ConvertTo-Json | Set-Content $jobFile
    Write-Host "Windows will ask for administrator rights to write Windows onto the VM's virtual disk."
    $code = Invoke-Elevated @("-ElevatedDisk", "`"$jobFile`"")
    $log = Join-Path $staging "disk.log"
    if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match "^\[\d\d:|Failed" } | ForEach-Object { Write-Host "  $_" } }
    if ($code -ne 0) { throw "Writing Windows to the virtual disk failed; see $log" }
    Remove-Item (Join-Path $staging "unattend.xml") # it holds the password in plain text

    Step "Creating the VM"
    $w, $h = $Resolution -split "x"
    $vm = New-VM -Name $vmName -Generation 2 -VHDPath $vhd -MemoryStartupBytes ([int64] $MemoryMB * 1MB) -Path $vmDir
    Set-VM -VM $vm -ProcessorCount $Processors -StaticMemory -CheckpointType Standard -AutomaticCheckpointsEnabled $false `
        -AutomaticStartAction Nothing -AutomaticStopAction TurnOff -Notes "Built $(Get-Date -Format s) from $(Split-Path $isoFile -Leaf) by tools/vmtest/vmtest.ps1"
    Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    Set-VMVideo -VM $vm -ResolutionType Single -HorizontalResolution ([uint16] $w) -VerticalResolution ([uint16] $h)
    Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter # no network: no updates, nothing leaves the VM
    # The VM's clock stops following this PC's, so the evaluation never sees time pass after the checkpoint.
    Get-VMIntegrationService -VM $vm | Where-Object { $_.Id -like "*2497F4DE-E9FA-4204-80E4-4B75C46419C0*" } | Disable-VMIntegrationService

    Step "Starting Windows for the first time (setup runs by itself, about 5 to 10 minutes)"
    Start-VM -VM $vm
    $s = Wait-GuestSession 30
    try {
        Step "Installing the virtual display driver and quieting the desktop"
        Invoke-Command -Session $s {
            $ErrorActionPreference = "Stop"
            # The driver's settings: one virtual monitor to start with.
            New-Item -ItemType Directory -Force "C:\VirtualDisplayDriver" | Out-Null
            (Get-Content "C:\vmtest\driver\vdd_settings.xml" -Raw) -replace "<count>\d+</count>", "<count>1</count>" |
                Set-Content "C:\VirtualDisplayDriver\vdd_settings.xml" -Encoding UTF8
            # Trust the driver's publisher inside this VM only, so installing it doesn't stop at a prompt.
            $signer = (Get-AuthenticodeSignature "C:\vmtest\driver\mttvdd.cat").SignerCertificate
            $store = New-Object Security.Cryptography.X509Certificates.X509Store "TrustedPublisher", "LocalMachine"
            $store.Open("ReadWrite"); $store.Add($signer); $store.Close()
            Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RootDevice {
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid cls, string desc, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int prop, byte[] buf, int size);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiCallClassInstaller(int fn, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("newdev.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hwid, string inf, int flags, out bool reboot);
    // Creates a root-enumerated device (like devcon install) and installs the driver on it.
    public static void Install(string inf, string hwid, string cls) {
        Guid g = new Guid(cls);
        IntPtr set = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        SP_DEVINFO_DATA d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
        if (!SetupDiCreateDeviceInfoW(set, "Display", ref g, null, IntPtr.Zero, 1, ref d)) throw new Exception("create " + Marshal.GetLastWin32Error());
        byte[] ids = System.Text.Encoding.Unicode.GetBytes(hwid + "\0\0");
        if (!SetupDiSetDeviceRegistryPropertyW(set, ref d, 1, ids, ids.Length)) throw new Exception("hardware id " + Marshal.GetLastWin32Error());
        if (!SetupDiCallClassInstaller(0x19, set, ref d)) throw new Exception("register " + Marshal.GetLastWin32Error());
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, inf, 1, out reboot)) throw new Exception("driver " + Marshal.GetLastWin32Error());
    }
}
"@
            [RootDevice]::Install("C:\vmtest\driver\MttVDD.inf", "Root\MttVDD", "4D36E968-E325-11CE-BFC1-08002BE10318")

            # Nothing that would cover the screen or interrupt a test.
            & powercfg /change monitor-timeout-ac 0; & powercfg /change standby-timeout-ac 0; & powercfg /hibernate off
            $policies = "HKLM:\SOFTWARE\Policies\Microsoft\Windows"
            New-Item -Force "$policies\Personalization" | Out-Null
            Set-ItemProperty "$policies\Personalization" NoLockScreen 1 -Type DWord
            New-Item -Force "$policies\CloudContent" | Out-Null
            Set-ItemProperty "$policies\CloudContent" DisableWindowsConsumerFeatures 1 -Type DWord
            Set-ItemProperty "$policies\CloudContent" DisableSoftLanding 1 -Type DWord
            New-Item -Force "HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications" | Out-Null
            Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications" ToastEnabled 0 -Type DWord
            New-Item -Force "HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" | Out-Null
            Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" ScoobeSystemSettingEnabled 0 -Type DWord

            # The task that runs scenarios on the VM's desktop as the signed-in user, not elevated.
            # conhost --headless: no console window that would become one of the windows under test.
            $action = New-ScheduledTaskAction -Execute "conhost.exe" -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\vmtest\runner.ps1"
            $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)
            Register-ScheduledTask -TaskName "vmtest" -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        }

        # A first scenario checks that the desktop, the monitors and the keyboard path work.
        $probe = Join-Path $PSScriptRoot "scenarios\probe.ps1"
        $code = Invoke-Scenario $s $probe $null (Join-Path $Root "results\$(Get-Date -Format yyyyMMdd-HHmmss)-build-probe") 2
        if ($code -ne 0) { throw "The new VM didn't pass its first check; see .vmtest\results\*-build-probe. Fix, then run with -Rebuild." }
        Invoke-Command -Session $s { Remove-Item -Recurse -Force "C:\vmtest\out", "C:\vmtest\keys" -ErrorAction SilentlyContinue }
    }
    finally { Remove-PSSession $s }

    Step "Saving the 'ready' checkpoint"
    Checkpoint-VM -VM $vm -SnapshotName "ready"
    Stop-VM -VM $vm -TurnOff -Force
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
    Step "The test VM is ready."
}

# ---- Building the app ----
function Build-App {
    $out = Join-Path $Root "build"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    $aot = $false
    $installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    $vswhere = Join-Path $installer "vswhere.exe"
    if ((Test-Path $vswhere) -and (& $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)) {
        $aot = $true
        if ($env:PATH -notlike "*$installer*") { $env:PATH += ";$installer" }
    }
    Step "Building the app ($(if ($aot) { 'NativeAOT' } else { '.NET, self-contained: the VM has no .NET runtime' }))"
    $publish = @("publish", (Join-Path $repo "src/AltTabEverywhere/AltTabEverywhere.csproj"), "-c", "Release", "-r", "win-x64", "-p:DebugType=none", "-o", $out)
    $publish += if ($aot) { "-p:PublishAot=true" } else { @("--self-contained", "true") }
    & dotnet @publish | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The build failed. Run 'dotnet build' to see why." }
    if ($WithSettings) {
        & dotnet publish (Join-Path $repo "src/AltTabEverywhere.Settings/AltTabEverywhere.Settings.csproj") -c Release -r win-x64 `
            --self-contained true -p:DebugType=none -o $out | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The settings window's build failed. Run 'dotnet build' to see why." }
    }
    Remove-Item (Join-Path $out "*.pdb") -ErrorAction SilentlyContinue
    return $out
}

# ---- Main ----
$scenarioFile = if (Test-Path $Scenario) { (Resolve-Path $Scenario).Path } else { Join-Path $PSScriptRoot "scenarios\$Scenario.ps1" }
if (-not (Test-Path $scenarioFile)) {
    $known = (Get-ChildItem (Join-Path $PSScriptRoot "scenarios\*.ps1") | ForEach-Object BaseName) -join ", "
    throw "No scenario '$Scenario'. Known: $known"
}

$vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
$ready = $vm -and (Get-VMSnapshot -VMName $vmName -Name "ready" -ErrorAction SilentlyContinue) -and (Test-Path $credFile)
if ($Rebuild -or -not $ready) { New-TestVM; $vm = Get-VM -Name $vmName }

$app = if ($AppDir) { (Resolve-Path $AppDir).Path } else { Build-App }

Step "Restoring the 'ready' checkpoint"
if ($vm.State -ne "Off") { Stop-VM -VM $vm -TurnOff -Force }
Get-VMSnapshot -VMName $vmName -Name "ready" | Restore-VMSnapshot -Confirm:$false
Start-VM -VM $vm
$session = Wait-GuestSession 5
$result = Join-Path $Root "results\$(Get-Date -Format yyyyMMdd-HHmmss)-$([IO.Path]::GetFileNameWithoutExtension($scenarioFile))-$($Monitors)mon"
try {
    Set-GuestMonitors $session $Monitors
    $code = Invoke-Scenario $session $scenarioFile $app $result $Monitors
}
finally {
    Remove-PSSession $session
    if (-not $KeepRunning) { Stop-VM -VM $vm -TurnOff -Force }
}
$verdict = switch ($code) { 0 { "PASS" } 2 { "TIMED OUT" } default { "FAIL" } }
Step "$verdict. Results: $result"
exit $code
