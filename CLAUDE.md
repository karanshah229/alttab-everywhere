# AltTab Everywhere: notes for Claude

## Resource budget (top priority)

This app runs all the time, from Windows startup, usually on a laptop. Every change must keep its **memory, CPU, battery and other resource use as low as possible**.

- **Do no work while idle.** No polling or periodic timers. React only to the keyboard hook and to system events.
- **Release everything a switcher session allocates when it closes:** DWM thumbnails, tiles, bitmaps. Keep caches capped.
- **Measure before and after any change** that could affect idle cost, and report the numbers: working set, private bytes, CPU time over an idle minute. Use the `--measure` flag where it exists, or Process counters.
- **State trade-offs openly**, for example keeping overlays alive to open faster versus freeing them to save memory. Prefer the lighter option unless the switcher becomes noticeably slower to open.

## Workflow

- Build: `dotnet build`. Unit tests: `dotnet test`.
- Navigation self-test on the real overlays, all monitors: run the app with `--selftest` (exit code 0 means pass; details in `%LOCALAPPDATA%\AltTabEverywhere\log.txt`). Run it after any change to layout or navigation.
- Stop the running `AltTabEverywhere.exe` before building; the running exe locks the build output.
- Commit to `main` (the user's choice); end commit messages with the Co-Authored-By line.
