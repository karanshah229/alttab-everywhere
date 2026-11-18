<#
.SYNOPSIS
  Builds the release zips, their SHA-256 checksums and the winget manifests.

.DESCRIPTION
  Four zips per version, each with the switcher (AltTabEverywhere.exe) and the settings window
  (AltTabEverywhere.Settings.exe):
    AltTabEverywhere-<v>-win-x64.zip / -win-arm64.zip
        The settings window uses the .NET 10 Desktop Runtime, shared with other apps (smallest).
    AltTabEverywhere-<v>-win-x64-selfcontained.zip / -win-arm64-selfcontained.zip
        The settings window carries the runtime inside it, for PCs without .NET 10.
  The switcher is native code (NativeAOT) in all four: no .NET runtime, the least memory. That needs
  Visual Studio's C++ build tools for the architecture; without them it falls back to a .NET build
  (framework-dependent, or single-file self-contained) and says so.
  ReadyToRun is deliberately off: measured, it didn't open the switcher faster and made the
  download bigger.

.EXAMPLE
  ./tools/release/build-release.ps1 -Version 0.7.0 -Output artifacts
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Output = "artifacts",
    [string] $Repository = "karanshah229/alttab-everywhere"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$project = Join-Path $root "src/AltTabEverywhere/AltTabEverywhere.csproj"
$settingsProject = Join-Path $root "src/AltTabEverywhere.Settings/AltTabEverywhere.Settings.csproj"
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = Resolve-Path $Output
$staging = Join-Path $Output "staging"

$variants = @(
    @{ Rid = "win-x64";   SelfContained = $false; Suffix = "" },
    @{ Rid = "win-arm64"; SelfContained = $false; Suffix = "" },
    @{ Rid = "win-x64";   SelfContained = $true;  Suffix = "-selfcontained" },
    @{ Rid = "win-arm64"; SelfContained = $true;  Suffix = "-selfcontained" }
)

# NativeAOT needs the C++ linker for each target architecture, found through vswhere.
$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $installer "vswhere.exe"
if ($env:PATH -notlike "*$installer*") { $env:PATH += ";$installer" } # the AOT build looks for vswhere on PATH
function CanAot($rid) {
    $component = if ($rid -eq "win-arm64") { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" } else { "Microsoft.VisualStudio.Component.VC.Tools.x86.x64" }
    (Test-Path $vswhere) -and [bool](& $vswhere -products * -requires $component -property installationPath)
}

$zips = @()
foreach ($v in $variants) {
    $name = "AltTabEverywhere-$Version-$($v.Rid)$($v.Suffix)"
    $dir = Join-Path $staging $name
    $common = @("-c", "Release", "-r", $v.Rid, "-o", $dir, "-p:Version=$Version", "-p:DebugType=none")
    $runtime = if ($v.SelfContained) { @("--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") }
               else { @("--self-contained", "false") }

    # The switcher.
    $switcher = @("publish", $project) + $common
    if (CanAot $v.Rid) { $switcher += "-p:PublishAot=true" }
    else {
        Write-Warning "$name`: no C++ build tools for $($v.Rid), so the switcher is a .NET build"
        $switcher += $runtime
    }
    & dotnet @switcher
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name" }

    # The settings window: a WPF exe of its own, next to the switcher.
    & dotnet publish $settingsProject @common @runtime
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name (settings window)" }

    Remove-Item (Join-Path $dir "*.pdb") -ErrorAction SilentlyContinue # debug symbols (NativeAOT writes them anyway)
    Copy-Item (Join-Path $root "LICENSE") $dir
    $zip = Join-Path $Output "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $dir "*") -DestinationPath $zip
    $zips += $zip
}

$sums = foreach ($zip in $zips) { "{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $zip -Leaf) }
Set-Content -Path (Join-Path $Output "SHA256SUMS.txt") -Value $sums -Encoding ascii

# winget: the framework-dependent zips, with the .NET 10 Desktop Runtime as a dependency winget installs.
$id = "AltTabEverywhere.AltTabEverywhere"
$wingetDir = Join-Path $Output "winget/$Version"
New-Item -ItemType Directory -Force $wingetDir | Out-Null
function HashOf($rid) { (Get-FileHash (Join-Path $Output "AltTabEverywhere-$Version-$rid.zip") -Algorithm SHA256).Hash }
$url = "https://github.com/$Repository/releases/download/v$Version"

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $wingetDir "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: AltTabEverywhere.exe
  PortableCommandAlias: alttab-everywhere
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Microsoft.DotNet.DesktopRuntime.10
MinimumOSVersion: 10.0.19041.0
Installers:
- Architecture: x64
  InstallerUrl: $url/AltTabEverywhere-$Version-win-x64.zip
  InstallerSha256: $(HashOf "win-x64")
- Architecture: arm64
  InstallerUrl: $url/AltTabEverywhere-$Version-win-arm64.zip
  InstallerSha256: $(HashOf "win-arm64")
ManifestType: installer
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $wingetDir "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: AltTab Everywhere contributors
PublisherUrl: https://github.com/$Repository
PackageName: AltTab Everywhere
PackageUrl: https://github.com/$Repository
License: MIT
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ShortDescription: An Alt+Tab switcher for Windows that shows on every monitor at once.
Description: A lightweight Alt+Tab replacement for Windows 10 and 11 that appears on every monitor, with live previews, search, app stacks and many settings. Does no work while idle.
Tags:
- alt-tab
- window-switcher
- multi-monitor
- productivity
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $wingetDir "$id.locale.en-US.yaml") -Encoding utf8

Remove-Item -Recurse -Force $staging
Write-Host "Built:"
Get-ChildItem $Output -Recurse -File | ForEach-Object { Write-Host ("  {0}  ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB)) }
