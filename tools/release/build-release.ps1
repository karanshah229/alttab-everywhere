<#
.SYNOPSIS
  Builds the release zips, their SHA-256 checksums and the winget manifests.

.DESCRIPTION
  Four zips per version:
    AltTabEverywhere-<v>-win-x64.zip / -win-arm64.zip
        Framework-dependent: small, shares the .NET 10 Desktop Runtime with other apps (lightest).
    AltTabEverywhere-<v>-win-x64-selfcontained.zip / -win-arm64-selfcontained.zip
        One exe with the runtime inside, for PCs without .NET 10.
  ReadyToRun is deliberately off: measured, it didn't open the switcher faster (WPF itself is
  already precompiled) and made the download bigger.

.EXAMPLE
  ./tools/release/build-release.ps1 -Version 0.6.0 -Output artifacts
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Output = "artifacts",
    [string] $Repository = "karanshah229/alttab-everywhere"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$project = Join-Path $root "src/AltTabEverywhere/AltTabEverywhere.csproj"
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = Resolve-Path $Output
$staging = Join-Path $Output "staging"

$variants = @(
    @{ Rid = "win-x64";   SelfContained = $false; Suffix = "" },
    @{ Rid = "win-arm64"; SelfContained = $false; Suffix = "" },
    @{ Rid = "win-x64";   SelfContained = $true;  Suffix = "-selfcontained" },
    @{ Rid = "win-arm64"; SelfContained = $true;  Suffix = "-selfcontained" }
)

$zips = @()
foreach ($v in $variants) {
    $name = "AltTabEverywhere-$Version-$($v.Rid)$($v.Suffix)"
    $dir = Join-Path $staging $name
    $publish = @("publish", $project, "-c", "Release", "-r", $v.Rid, "-o", $dir, "-p:Version=$Version", "-p:DebugType=none")
    if ($v.SelfContained) { $publish += @("--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") }
    else { $publish += @("--self-contained", "false") }
    & dotnet @publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name" }
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
