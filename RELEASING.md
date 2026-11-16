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
| `AltTabEverywhere-<v>-win-x64.zip`, `-win-arm64.zip` | Most people. Needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); shares it with other apps, so it's the lightest. About 250 KB |
| `AltTabEverywhere-<v>-win-x64-selfcontained.zip`, `-win-arm64-selfcontained.zip` | PCs without .NET 10. One exe with the runtime inside, about 65 MB zipped (170 MB unzipped), and about 3 MB more private memory |
| `SHA256SUMS.txt` | Checksums of the zips |

The winget package uses the small zips and installs the .NET 10 Desktop Runtime as a dependency.

ReadyToRun is off on purpose: measured, it didn't open the switcher any faster (WPF itself is already precompiled; this app's own code is small) and it made the download bigger.
