# AltTab Everywhere: notes for Claude

## Core philosophies

Every change follows these three rules, in this order when they conflict:

1. **Lowest resource use possible.** It's an always-on app; see the resource budget below.
2. **As many configuration options as possible.** If a behaviour could reasonably go either way, make it a setting with a sensible default instead of hard-coding it. Document every setting in the README's settings table.
3. **Open source, built on your machine.** Everything you run, you built from code you can read: `install.ps1` builds and installs it, and `install.ps1 -AdminWindows` signs it on the user's own PC (so no code-signing certificate is ever bought). Ready-made downloads exist only for convenience. Develop in the open on GitHub (`karanshah229/alttab-everywhere`). No telemetry, no closed components, no features that need a paid service.

## Working agreement

The user tests when asked. Otherwise: develop, test (unit tests, `--selftest`, `--measure` where relevant), commit and push without waiting. Ask when genuinely unsure about product decisions.

## Resource budget (top priority)

This app runs all the time, from Windows startup, usually on a laptop. Every change must keep its **memory, CPU, battery and other resource use as low as possible**.

- **Do no work while idle.** No polling or periodic timers. React only to the keyboard hook and to system events.
- **Release everything a switcher session allocates when it closes:** DWM thumbnails, tiles, bitmaps. Keep caches capped.
- **Measure before and after any change** that could affect idle cost, and report the numbers: working set, private bytes, CPU time over an idle minute. Use the `--measure` flag where it exists, or Process counters.
- **State trade-offs openly**, for example keeping overlays alive to open faster versus freeing them to save memory. Prefer the lighter option unless the switcher becomes noticeably slower to open.

## Workflow

- Build: `dotnet build` (both exes: the switcher and `AltTabEverywhere.Settings`). Unit tests: `dotnet test`.
- Native build check: `dotnet publish src/AltTabEverywhere -c Release -r win-x64 -p:PublishAot=true` (needs Visual Studio's C++ tools; in Git Bash add `/c/Program Files (x86)/Microsoft Visual Studio/Installer` to PATH for vswhere). It must stay free of trim/AOT warnings: no reflection-based JSON, no runtime COM (`[ComImport]`); use `Interop/Com.cs` or source-generated COM. Run `--selftest` on the published exe after interop changes.
- Navigation self-test on the real overlays, all monitors: run the app with `--selftest` (exit code 0 means pass; details in `%LOCALAPPDATA%\AltTabEverywhere\log.txt`). Run it after any change to layout or navigation.
- Stop the running `AltTabEverywhere.exe` before building; the running exe locks the build output.
- The user runs an installed copy: after a change is done and tested, update it with `install.ps1` (or `install.ps1 -AdminWindows` if that is how it was installed; that needs their administrator prompt, so ask them to run it). Never add certificates or trust anything on the user's machine yourself.
- Commit to `main` (the user's choice); end commit messages with the Co-Authored-By line.
