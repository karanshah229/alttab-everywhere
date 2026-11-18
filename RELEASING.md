# Releasing

1. Set `<Version>` in `src/AltTabEverywhere/AltTabEverywhere.csproj` to the new version and commit.
2. Tag and push:

   ```bash
   git tag v0.6.0
   ```

   ```bash
   git push origin v0.6.0
   ```

3. The **Release** workflow runs the unit tests, builds the zips, writes `SHA256SUMS.txt` and the winget manifests, and opens a **draft** release with the zips attached. Check it, edit the notes, and publish it.
4. winget: download the `winget-manifests` artifact from the workflow run. It has the three manifests for this version, already pointing at the release's zips with their hashes. Submit them to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), for example with `wingetcreate submit <folder>`, or by a pull request that adds them under `manifests/a/AltTabEverywhere/AltTabEverywhere/<version>/`. `winget validate --manifest <folder>` checks them first.

To build the same files locally:

```bash
pwsh tools/release/build-release.ps1 -Version 0.6.0 -Output artifacts
```

## What a release contains

| File | For |
|---|---|
| `AltTabEverywhere-<v>-win-x64.zip`, `-win-arm64.zip` | Most people. The switcher is native code (no .NET needed); the settings window needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), shared with other apps. About 3.5 MB |
| `AltTabEverywhere-<v>-win-x64-selfcontained.zip`, `-win-arm64-selfcontained.zip` | PCs without .NET 10: the settings window carries the runtime inside it. About 70 MB zipped. The switcher is the same native exe |
| `SHA256SUMS.txt` | Checksums of the zips |

The winget package uses the small zips and installs the .NET 10 Desktop Runtime as a dependency (for the settings window).

The switcher is built with NativeAOT, which needs Visual Studio's C++ build tools for each architecture (GitHub's Windows runners have them). Without them, `build-release.ps1` warns and falls back to a .NET build of the switcher for that zip.

ReadyToRun is off on purpose for the settings window: measured, it didn't open any faster and made the download bigger.
