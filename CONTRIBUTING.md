# Contributing to AltTab Everywhere

Thanks for helping. Bug reports, ideas, and pull requests are all welcome.

## The three rules

Every change follows these, in this order when they conflict:

1. **Lowest resource use possible.** The app runs all the time, usually on a laptop. It must do no work while idle: no polling, no timers. It reacts only to the keyboard hook and to system events. Everything a switcher session creates (previews, tiles, bitmaps) is released when the session closes.
2. **As many configuration options as possible.** If a behaviour could reasonably go either way, make it a setting with a sensible default. Every setting goes in the README's settings table and in the settings window.
3. **Open source by default.** No telemetry, no closed components, no features that need a paid service.

## Build and test

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet test
```

Stop a running `AltTabEverywhere.exe` before you build: it locks the build output.

Two more checks run the real app:

- **`--selftest`** drives the real switchers on every monitor: navigation, layouts, stacks, search, badges, accessibility, window actions and more. Exit code 0 means every check passed; details go to `%LOCALAPPDATA%\AltTabEverywhere\log.txt`. Run it after any change to layout, navigation or the switcher's behaviour. Some checks need two monitors and are skipped with one.
- **`--measure`** times opening the switcher and logs memory before and after 50 sessions. Run it before and after any change that could affect speed or idle cost, and put both sets of numbers in your pull request.

```bash
dotnet run --project src/AltTabEverywhere -c Release -- --selftest
```

The keyboard hook ignores simulated key presses, so the self-test can't press Alt+Tab for you. Keyboard behaviour needs a quick try by hand.

## Pull requests

- Keep each pull request to one change, with tests: unit tests for logic, self-test checks for anything on screen.
- Say what you measured (working set, private bytes, CPU over an idle minute, open time) if the change could affect them.
- New behaviour that someone might not want gets a setting, documented in the README.
- Match the code around you: comments explain *why*, not *what*.

## Reporting bugs

Use the bug report form. The most useful things to include:

- what you did, what you expected, and what happened;
- your Windows version and your monitors (how many, which is the main one, their scaling);
- the relevant lines of `%LOCALAPPDATA%\AltTabEverywhere\log.txt`. The log never contains window titles, but check it before posting anyway.

`--list` writes the window list (with titles) to `windows.txt` in the same folder. Only share it if you're happy to.

## Licence

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
