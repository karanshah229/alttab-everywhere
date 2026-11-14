# AltTab Everywhere

An Alt+Tab switcher for Windows 11 that appears on **every monitor at once**. Windows' own switcher only ever shows on the main display.

> Status: **prototype (v0.1)**. The switcher works, but settings are hard-coded and it hasn't been tested much.

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
| Arrow keys (Alt held) | Move the selection |
| Release Alt, or press Enter | Switch to the selected window |
| Esc | Close without switching |
| Click a tile | Switch to that window |

A quick Alt+Tab tap switches to the previous window without showing the switcher, as Windows does.

## How it works

| Piece | API | File |
|---|---|---|
| Take over Alt+Tab | `SetWindowsHookEx(WH_KEYBOARD_LL)`; return 1 to swallow Tab | `KeyboardHook.cs` |
| Stop the app's menu bar opening when Alt is released | `SendInput` of an unassigned key (0xE8) while Alt is held | `WindowActivator.cs` |
| List the windows | The shell's own Alt+Tab list: undocumented `IApplicationViewCollection` → `IApplicationView::GetShowInSwitchers`, sorted by `GetLastActivationTimestamp`. This matches Windows exactly | `Interop/ShellViews.cs` |
| Fallback list | `EnumWindows`, `DWMWA_CLOAKED`, Raymond Chen's owner/popup rule. Used only if the shell interfaces fail | `WindowEnumerator.cs` |
| One overlay per monitor | `EnumDisplayMonitors`, `GetMonitorInfo`, `GetDpiForMonitor`; Per-Monitor V2 DPI manifest | `Monitors.cs`, `SwitcherWindow.cs` |
| Live previews | `DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties` | `SwitcherWindow.cs` |
| Switch windows | `SetForegroundWindow`, with an `AttachThreadInput` fallback | `WindowActivator.cs` |

To check the window list against Windows, run `--list`. It writes both lists, the shell list and the fallback filter, to `%LOCALAPPDATA%\AltTabEverywhere\windows.txt`. This file contains window titles.

## Known limitations

- **Undocumented shell interfaces:** their GUIDs and method layouts change between Windows releases. Only methods verified on build 26200 are declared; a later method, `IsTray`, is at a different position on that build and crashes. If a future build breaks the interfaces, the app falls back to the heuristic filter and logs `window list source: heuristic filter`. The fallback lists some hidden windows, such as apps that hide themselves via `ITaskbarList::DeleteTab` (for example Superwhisper).

- **Elevated (admin) windows:** while one has focus, the hook receives no keys, so Windows' own Alt+Tab appears instead. Fixing this needs a signed build with `uiAccess="true"` installed under Program Files.
- **Minimized windows** show their icon instead of a live preview.
- **Store/UWP apps** may show a generic icon.
- If the UI thread stalls, Windows can silently remove the hook. The app keeps running, but Windows' own Alt+Tab returns.
- Tested on one monitor so far; not yet tested with a second monitor connected.

## Roadmap

- [ ] Settings file (JSON) and a settings window
- [ ] Group windows of the same app (stacked) or list each one separately
- [ ] Sticky mode: keep the switcher open after releasing Alt
- [ ] Turn previews on or off; set preview size
- [ ] Per-monitor filter: each monitor lists only its own windows
- [ ] Choose which monitors show the switcher (all / under cursor / where the active window is)
- [ ] Type to search window titles
- [ ] Close a window from the switcher (Delete key or middle-click)
- [ ] Start with Windows
- [ ] Re-install the hook if Windows removes it
- [ ] Signed release with `uiAccess` so it works over admin windows
- [ ] Choose a licence before publishing
