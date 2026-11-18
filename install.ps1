<#
.SYNOPSIS
  Builds AltTab Everywhere from this source code and installs it on this PC.

.DESCRIPTION
  The recommended way to install: everything you run, you built from code you can read.

  Without switches: a per-user install in %LOCALAPPDATA%\Programs\AltTab Everywhere. No admin
  rights needed. Works everywhere except while an administrator window has focus (Windows then
  keeps the keyboard from every app that isn't elevated, so its own Alt+Tab appears).

  -AdminWindows: also works while an administrator window has focus. Windows only allows that
  ("uiAccess") for a signed exe under Program Files, so with one administrator prompt this:
    1. builds the uiAccess variant;
    2. installs it in C:\Program Files\AltTab Everywhere;
    3. creates a code-signing certificate for this PC only, trusts it on this PC, signs the exe;
    4. deletes the certificate's private key at once, so nothing else can ever be signed with it.
  Each run makes a fresh certificate and removes the previous one. uninstall.ps1 removes it too.

  The switcher is compiled to native code (NativeAOT: about 13 MB of memory instead of 18, and no
  .NET runtime to load) when Visual Studio's C++ build tools are installed ("Desktop development
  with C++", or the Build Tools for Visual Studio); otherwise, or with -NoNativeAot, it's a normal
  .NET build. The settings window is always a normal .NET (WPF) app, started only while it's open.

  Your settings (%APPDATA%\AltTabEverywhere) are kept. Needs the .NET 10 SDK.

.EXAMPLE
  ./install.ps1
.EXAMPLE
  ./install.ps1 -AdminWindows
.EXAMPLE
  ./install.ps1 -NoNativeAot
#>
param(
    [switch] $AdminWindows,
    # Build the switcher as a normal .NET app even if the C++ build tools for NativeAOT are installed.
    [switch] $NoNativeAot,
    # Internal: the elevated second half of -AdminWindows (copy, sign, trust). Not for direct use.
    [string] $ElevatedInstallFrom
)
$ErrorActionPreference = "Stop"
$appName = "AltTab Everywhere"
$userDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$adminDir = Join-Path $env:ProgramFiles $appName
$certSubject = "CN=AltTab Everywhere local build ($env:COMPUTERNAME)"

function Copy-Build($from, $to) {
    if (Test-Path $to) { Remove-Item -Recurse -Force (Join-Path $to "*") }
    else { New-Item -ItemType Directory -Force $to | Out-Null }
    Copy-Item -Recurse (Join-Path $from "*") $to
}

# ---- Elevated half of -AdminWindows: runs in its own administrator PowerShell. ----
if ($ElevatedInstallFrom) {
    try {
        Copy-Build $ElevatedInstallFrom $adminDir
        # Only ever our own certificates, by their exact subject: earlier local builds' ones.
        foreach ($store in "Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPublisher") {
            Get-ChildItem $store | Where-Object Subject -eq $certSubject | Remove-Item
        }
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(20)
        $public = Join-Path $env:TEMP "alttab-everywhere-local.cer"
        try {
            Export-Certificate -Cert $cert -FilePath $public | Out-Null
            Import-Certificate -FilePath $public -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
            Import-Certificate -FilePath $public -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
            # The switcher: one native exe (NativeAOT), or the exe and its .dll (.NET).
            foreach ($file in "AltTabEverywhere.exe", "AltTabEverywhere.dll") {
                if (-not (Test-Path (Join-Path $adminDir $file))) { continue }
                $signed = Set-AuthenticodeSignature -FilePath (Join-Path $adminDir $file) -Certificate $cert -HashAlgorithm SHA256
                if ($signed.Status -ne "Valid") { throw "Signing $file failed: $($signed.StatusMessage)" }
            }
        }
        finally {
            # The private key goes now: only these files are ever signed with it.
            Remove-Item $cert.PSPath -DeleteKey -ErrorAction SilentlyContinue
            Remove-Item $public -ErrorAction SilentlyContinue
        }
        Write-Host "Installed and signed in $adminDir"
        exit 0
    }
    catch {
        Write-Host "Install failed: $_" -ForegroundColor Red
        Read-Host "Press Enter to close"
        exit 1
    }
}

# ---- Normal run: build as you, then install. ----
$root = $PSScriptRoot
if (-not $AdminWindows -and (Test-Path (Join-Path $adminDir "AltTabEverywhere.exe"))) {
    throw "It's installed for administrator windows too (in $adminDir). Update that copy with: ./install.ps1 -AdminWindows  (or remove it first with ./uninstall.ps1)"
}
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^(1\d|[2-9]\d)\.' })) {
    throw "The .NET 10 SDK is needed: https://dotnet.microsoft.com/download/dotnet/10.0"
}
$rid = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
$staging = Join-Path $env:TEMP "alttab-everywhere-build-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
# NativeAOT needs the C++ linker for this PC's architecture, found through vswhere.
$aot = $false
if (-not $NoNativeAot) {
    $installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    $vswhere = Join-Path $installer "vswhere.exe"
    $component = if ($rid -eq "win-arm64") { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" } else { "Microsoft.VisualStudio.Component.VC.Tools.x86.x64" }
    if ((Test-Path $vswhere) -and (& $vswhere -products * -requires $component -property installationPath)) {
        $aot = $true
        if ($env:PATH -notlike "*$installer*") { $env:PATH += ";$installer" } # the AOT build looks for vswhere on PATH
    }
}
$publish = @("publish", (Join-Path $root "src/AltTabEverywhere/AltTabEverywhere.csproj"), "-c", "Release", "-r", $rid,
    "-p:DebugType=none", "-o", $staging)
$publish += if ($aot) { "-p:PublishAot=true" } else { @("--self-contained", "false") }
if ($AdminWindows) { $publish += "-p:UiAccess=true" }
Write-Host "Building ($rid, $(if ($aot) { 'native code (NativeAOT)' } else { '.NET' })$(if ($AdminWindows) { ', uiAccess' }))..."
if (-not $aot -and -not $NoNativeAot) {
    Write-Host "  (Install Visual Studio's C++ build tools to get the lighter native build.)"
}
& dotnet @publish | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The build failed. Run 'dotnet build' to see why." }
# The settings window: a WPF exe of its own, next to the switcher, started only while you use it.
& dotnet publish (Join-Path $root "src/AltTabEverywhere.Settings/AltTabEverywhere.Settings.csproj") -c Release -r $rid `
    --self-contained false -p:DebugType=none -o $staging | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The settings window's build failed. Run 'dotnet build' to see why." }
Remove-Item (Join-Path $staging "*.pdb") -ErrorAction SilentlyContinue # debug symbols (NativeAOT writes them anyway)
Copy-Item (Join-Path $root "LICENSE") $staging

# The running copy (wherever it was started from) locks its files and would keep running the old build.
Get-Process AltTabEverywhere, AltTabEverywhere.Settings -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

try {
    if ($AdminWindows) {
        Write-Host "Windows will ask for administrator rights to install under Program Files and sign the exe."
        $self = Join-Path $root "install.ps1"
        $elevated = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$self`"", "-ElevatedInstallFrom", "`"$staging`"")
        if ($elevated.ExitCode -ne 0) { throw "The administrator step didn't finish." }
        $target = $adminDir
        # A per-user copy from an earlier install would otherwise linger.
        if (Test-Path $userDir) { Remove-Item -Recurse -Force $userDir }
    }
    else {
        Copy-Build $staging $userDir
        $target = $userDir
    }
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

# Started as you, not as administrator. If "Start with Windows" is on, the app points it here itself.
$exe = Join-Path $target "AltTabEverywhere.exe"
Start-Process $exe
Write-Host ""
Write-Host "AltTab Everywhere is installed in $target and running (see the notification area)."
if (-not $AdminWindows) {
    Write-Host "To make it work while an administrator window has focus too: ./install.ps1 -AdminWindows"
}
