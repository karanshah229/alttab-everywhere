<#
.SYNOPSIS
  Removes what install.ps1 installed.

.DESCRIPTION
  Stops the app, removes the per-user install and its Start-with-Windows entry, and, if
  install.ps1 -AdminWindows was used, the Program Files copy and this PC's local signing
  certificate (one administrator prompt). Your settings and log are kept unless you add
  -RemoveSettings.

.EXAMPLE
  ./uninstall.ps1
.EXAMPLE
  ./uninstall.ps1 -RemoveSettings
#>
param(
    [switch] $RemoveSettings,
    # Internal: the elevated half (Program Files copy and certificates). Not for direct use.
    [switch] $Elevated
)
$ErrorActionPreference = "Stop"
$appName = "AltTab Everywhere"
$userDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$adminDir = Join-Path $env:ProgramFiles $appName
$certSubject = "CN=AltTab Everywhere local build ($env:COMPUTERNAME)"
$stores = "Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPublisher"

function Our-Certificates { Get-ChildItem $stores | Where-Object Subject -eq $certSubject }

if ($Elevated) {
    if (Test-Path $adminDir) { Remove-Item -Recurse -Force $adminDir }
    Our-Certificates | Remove-Item
    exit 0
}

Get-Process AltTabEverywhere, AltTabEverywhere.Settings -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Start with Windows: only our entry, and only if it points at an installed copy.
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$entry = (Get-ItemProperty $run -ErrorAction SilentlyContinue).AltTabEverywhere
if ($entry -and ($entry -like "*$userDir*" -or $entry -like "*$adminDir*")) {
    Remove-ItemProperty $run -Name AltTabEverywhere
}

if (Test-Path $userDir) { Remove-Item -Recurse -Force $userDir }

if ((Test-Path $adminDir) -or (Our-Certificates)) {
    Write-Host "Windows will ask for administrator rights to remove the Program Files copy and the signing certificate."
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Elevated")
    if ($p.ExitCode -ne 0) { throw "The administrator step didn't finish." }
}

if ($RemoveSettings) {
    foreach ($dir in (Join-Path $env:APPDATA "AltTabEverywhere"), (Join-Path $env:LOCALAPPDATA "AltTabEverywhere")) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }
}
Write-Host "AltTab Everywhere is uninstalled$(if (-not $RemoveSettings) { '; your settings are kept' })."
