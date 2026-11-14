# AltTab Everywhere

An Alt+Tab switcher for Windows 11 that appears on **every monitor at once**. Windows' own switcher only ever shows on the main display.

> Status: **early (v0.3)**. Works day to day; settings are a JSON file and there's no settings window yet.

It's built to run all the time from startup, so it stays light. When idle it does no work (0 ms of CPU) and uses about 10–15 MB working set (about 40–50 MB private). The switcher appears about 105–130 ms after Alt+Tab. See [Resource use](#resource-use).

## Run it

Requires the .NET 10 SDK on Windows 10 or 11.

```bash
dotnet run --project src/AltTabEverywhere
```

The app runs in the notification area, with no main window. Right-click its tray icon to **pause** it (untick **Enabled**) or **Exit**. If the app closes or crashes, Windows' own Alt+Tab takes over again straight away.

To test without the keyboard, run it with `--demo`. It opens the switcher on every monitor for 4 seconds, writes the overlay layout to the log, and exits:

```bash
dotnet run --project src/AltTabEverywhere -- --demo
```

Log file: `%LOCALAPPDATA%\AltTabEverywhere\log.txt`. It records counts and layout only, never window titles.

## Keys

| Keys | Action |
|---|---|
| Alt+Tab / Alt+Shift+Tab | Open the switcher, then move forward or back |
| ← / → | Previous / next tile |
| ↑ / ↓ | Row up / down, wrapping around. Every monitor uses the same grid, so a row is a row on every screen |
| Alt+` / Alt+Shift+` | In stacked mode: cycle through the windows of the selected app. The badge shows "2/3" |
| Release Alt, or press Enter | Switch to the selected window |
| Esc | Close without switching |
| Click a tile | Switch to that window |
| Mouse wheel | Scroll, when there are more windows than fit |
| Click outside the switcher | Close it |

A quick Alt+Tab tap switches to the previous window straight away. The switcher appears as soon as it's drawn; set `showDelayMs` if you'd rather quick taps never show it.

The switcher takes at most 75% of a monitor's width and 75% of its height. With many windows, tiles first shrink slightly (to 75% of their size at most); after that the grid scrolls inside the switcher, and the selection always stays in view.

## Settings

Right-click the tray icon and choose **Edit settings…**. This opens `%APPDATA%\AltTabEverywhere\settings.json`, which is created with defaults on first run. Changes apply as soon as you save. If the file has a mistake, the switcher keeps your previous settings and writes the error to the log. Comments and trailing commas are allowed.

| Key | Default | Meaning |
|---|---|---|
| `showDelayMs` | `0` | Wait this long before showing the switcher, so taps shorter than this switch without it appearing. `0` shows it as soon as it's drawn |
| `showOn` | `"allMonitors"` | `"allMonitors"` or `"monitorUnderCursor"` |
| `groupWindows` | `"separate"` | `"separate"`: one tile per window. `"stacked"`: one tile per app, with a ×N badge; Alt+` picks a window inside the stack. Apps are told apart by their shell app ID, so Chrome profiles stay separate |
| `showThumbnails` | `true` | Live window previews. `false` shows icons only |
| `stayOpenAfterAltRelease` | `false` | Keep the switcher open after you release Alt. Choose with Enter or a click, move with Tab or the arrow keys, close with Esc. A quick tap still switches immediately |
| `tileWidth` | `240` | Tile width, 80–600. Tiles shrink slightly when there are many windows, then the grid scrolls |
| `thumbnailHeight` | `140` | Preview height, 50–400 |

## How it works

| Piece | API | File |
|---|---|---|
| Take over Alt+Tab | `SetWindowsHookEx(WH_KEYBOARD_LL)`; return 1 to swallow Tab | `KeyboardHook.cs` |
| Stop the app's menu bar opening when Alt is released | `SendInput` of an unassigned key (0xE8) while Alt is held | `WindowActivator.cs` |
| List the windows | The shell's own Alt+Tab list: undocumented `IApplicationViewCollection` → `IApplicationView::GetShowInSwitchers`, sorted by `GetLastActivationTimestamp`. This matches Windows exactly | `Interop/ShellViews.cs` |
| Fallback list | `EnumWindows`, `DWMWA_CLOAKED`, Raymond Chen's owner/popup rule. Used only if the shell interfaces fail | `WindowEnumerator.cs` |
| One overlay per monitor | `EnumDisplayMonitors`, `GetMonitorInfo`, `GetDpiForMonitor`; Per-Monitor V2 DPI manifest | `Monitors.cs`, `SwitcherWindow.cs` |
| Parallel overlays | Each overlay window on its own STA thread and dispatcher; calls are marshalled and stay in order | `OverlayHost.cs` |
| Live previews | `DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties`. DWM draws them on top of the window, so they are cropped to the scroll viewport by hand | `SwitcherWindow.cs` |
| Grid and scrolling | One column count for all monitors; 75% × 75% cap | `TileLayout.cs` |
| App stacks | Grouped by shell app ID, falling back to the executable path | `SwitcherItem.cs` |
| Switch windows | `SetForegroundWindow`, with an `AttachThreadInput` fallback | `WindowActivator.cs` |

## Resource use

The app is always running, so idle cost matters more than anything else. Measured on Windows 11 (build 26200) with two monitors, Release build:

| State | Working set | Private | Idle CPU |
|---|---|---|---|
| Fresh start (after warm-up and trim) | 15–16 MB | 41 MB | 0 ms per minute |
| Idle after 50 switcher opens | 3–12 MB | 52 MB | 0 ms per minute |

| Speed | Time |
|---|---|
| Alt+Tab until the switcher is on screen | ~105–130 ms with two monitors: list windows ~13 ms, both monitors build and draw in parallel (~65 ms), uncloak, then the next screen refresh |
| After plugging in or unplugging a monitor | the first open takes ~300 ms while the switcher windows are recreated |
| First Alt+Tab after the app starts | about the same (without the start-up warm-up: ~600 ms) |

How it stays light:
- **No polling.** It only reacts to the keyboard hook and to changes to the settings file.
- **Software rendering for WPF.** The overlay is simple, and previews are composited by DWM, so there's no need for a Direct3D device. That device alone cost about 115 MB private, about 1,200 handles, and a 1.5 MB native leak per open.
- **Memory trim 2 seconds after each close.** The .NET heap is compacted and the working set is handed back to Windows. This is a one-shot timer, not a repeating one.
- **Workstation GC without the background GC thread.**
- **The switcher is built the moment you press Alt+Tab, while still invisible** (DWM-cloaked). It's revealed once its first frame is drawn, so it never appears as an empty box. If you set a `showDelayMs`, the wait and the build overlap instead of adding up.
- **One UI thread per monitor.** Each monitor's switcher window runs on its own thread, so all of them build and draw at the same time. This saved ~35–50 ms with two monitors, for about +2 MB private memory and one thread per monitor.
- **Warm-up at startup.** The switcher is built once, invisibly, so the first Alt+Tab doesn't wait for .NET to compile the code.
- **The overlay windows are kept alive between opens.** Recreating them made each open about 60 ms slower and didn't lower committed memory.

To reproduce the numbers, run `--measure`. It logs the first-open time after warm-up, a breakdown of where each open's time goes, the Alt+Tab-to-screen time, and memory and handles across 50 opens. Add `--hardware-rendering` to compare against WPF's GPU rendering:

```bash
dotnet run -c Release --project src/AltTabEverywhere -- --measure
```

## Tests

Run the unit tests (grid movement, tile layout, grouping, settings parsing) with:

```bash
dotnet test
```

To test the real switcher, run `--selftest`. It opens the switcher on every monitor with 1 to 47 tiles, some of them app stacks, and checks:
- for every tile and every arrow key, where the selection lands against where the tiles actually are on each screen;
- that the selection is always scrolled into view;
- that the switcher stays within the 75% × 75% cap;
- that cycling a stack shows the right window without moving any tiles.

Results go to the log, and the exit code is 0 if every check passed:

```bash
dotnet run --project src/AltTabEverywhere -- --selftest
```

To check the window list against Windows, run `--list`. It writes both lists, the shell list and the fallback filter, to `%LOCALAPPDATA%\AltTabEverywhere\windows.txt`. This file contains window titles.

## Known limitations

- **Undocumented shell interfaces:** their GUIDs and method layouts change between Windows releases. Only methods verified on build 26200 are declared; a later method, `IsTray`, is at a different position on that build and crashes. If a future build breaks the interfaces, the app falls back to the heuristic filter and logs `window list source: heuristic filter`. The fallback lists some hidden windows, such as apps that hide themselves via `ITaskbarList::DeleteTab` (for example Superwhisper).

- **Elevated (admin) windows:** while one has focus, the hook receives no keys, so Windows' own Alt+Tab appears instead. Fixing this needs a signed build with `uiAccess="true"` installed under Program Files.
- **Minimized windows** show their icon instead of a live preview.
- **Store/UWP apps** may show a generic icon.
- **Alt+`** uses the key above Tab, wherever it is on your keyboard layout (`VK_OEM_3`). On some non-US layouts that key has a different symbol.
- If the UI thread stalls, Windows can silently remove the hook. The app keeps running, but Windows' own Alt+Tab returns.
- Tested with two monitors at different scaling (125% and 100%) using `--demo` and `--selftest`.

## Roadmap

- [x] Settings file (JSON) with live reload
- [ ] Settings window
- [x] Group windows of the same app (stacked) or list each one separately
- [x] Cap the switcher at 75% × 75% and scroll inside it
- [x] Sticky mode: keep the switcher open after releasing Alt
- [x] Turn previews on or off; set preview size
- [ ] Per-monitor filter: each monitor lists only its own windows
- [x] Choose which monitors show the switcher (all / under cursor). Still to do: where the active window is
- [ ] Type to search window titles
- [ ] Close a window from the switcher (Delete key or middle-click)
- [ ] Start with Windows
- [ ] Re-install the hook if Windows removes it
- [ ] Signed release with `uiAccess` so it works over admin windows
- [ ] Choose a licence before publishing
