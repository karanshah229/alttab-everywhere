# Roadmap

## Core philosophies

Every feature is judged against these, in this order when they conflict:

1. **Lowest resource use possible.** It runs all the time from startup. No idle work, no polling; memory is measured before and after.
2. **As many configuration options as possible.** If a behaviour could reasonably go either way, it's a setting with a sensible default.
3. **Open source by default.** Developed in the open. No telemetry, nothing closed, nothing that needs a paid service.

## How items are ranked

Features are grouped into phases and ranked within each phase in the order to build them:
1. What you'd feel day to day.
2. Effort (S = hours, M = a day or two, L = a week or more).
3. Cost to the resource budget. Anything that adds idle work is a non-starter.

Inspiration comes from:
- **Windows:** its own Alt+Tab, Alt-Tab Terminator, VistaSwitcher, DisplayFusion, Switcheroo, PowerToys window search, the Windhawk per-monitor Alt+Tab mod.
- **macOS:** Cmd+Tab, Cmd+`, AltTab for macOS, Witch, Contexts.
- **Linux:** GNOME's app and window switchers, the GNOME "Advanced Alt-Tab Window Switcher" extension, KDE Plasma's KWin task switcher.
- Gaps found while building this app.

| Column | Meaning |
|---|---|
| **Effort** | S / M / L |
| **Idle cost** | What the feature adds while the switcher is closed. *None* = nothing beyond code size |
| **Seen in** | Where the idea comes from. *Ours* = specific to this app's multi-monitor focus |

## Done

- [x] Switcher on every monitor at once (or only the one under the mouse)
- [x] Same window list as Windows' Alt+Tab (the shell's own list)
- [x] Live previews, per-monitor DPI, shared grid across monitors, arrow keys, rows, wrap-around
- [x] Stacked mode (one tile per app): pause on a stack (or press Alt+`, or click its badge) to see its windows, with a Back tile
- [x] Stay-open mode (like Windows' Ctrl+Alt+Tab)
- [x] 75% × 75% size cap with internal scrolling
- [x] JSON settings with live reload; tray icon (pause, edit settings, log, exit)
- [x] Low idle cost: 0 ms CPU, ~10–16 MB working set; opens in ~105–130 ms
- [x] Phase 1: start with Windows, hook recovery, close from the switcher, exclusions, desktops scope, per-monitor lists, MIT licence
- [x] Phase 2: type to search, several shortcuts with their own filters (including a current-app switcher), number keys, Home/End/Page Up/Page Down, hover to select
- [x] Phase 3: settings window, own icon, light/dark and accent colours, layout styles (previews, large icons, compact list, icons only), Store app icons, state badges (minimized, admin, not responding, other desktop), Peek (outline the selected window), "Show desktop" tile, accessibility (screen-reader names, high contrast, keyboard-only colour picker)
- [x] Phase 4: move the selected window to this monitor (Ctrl+Enter or Ctrl+click), window actions (minimize, maximize, always on top, quit app; keys and a right-click menu), sort orders (recent, stacking, app, title, monitor), previews for minimized windows
- [x] Phase 5: contributing guide, issue templates, CI, release packaging (zips, checksums, winget manifests), opt-in update check, README screenshots and GIF, build-on-your-machine install with local signing so it works over admin windows

---

## Phase 1: Daily-driver reliability (done)

These make it safe to leave running all day, every day. None adds idle work.

| # | Feature | Why | Effort | Idle cost | Seen in |
|---|---|---|---|---|---|
| 1 | **Start with Windows** (tray toggle and setting; per-user Run key, no admin) | An always-on app that has to be started by hand isn't always on | S | None | All alternatives |
| 2 | **Recover the keyboard hook automatically.** Re-install it on resume from sleep, unlock, session change and display change | Windows can silently drop a low-level hook. Event-driven, no polling | S–M | None | Ours |
| 3 | **Cap the log file size** (rotate at 1 MB) | The log currently grows forever | S | None | Ours |
| 4 | **Close a window from the switcher.** Delete key, middle-click, or an × on hover | Windows' Alt+Tab has Delete-to-close | S | None | Windows, Alt-Tab Terminator, AltTab (macOS) |
| 5 | **Exclude apps and titles** (settings lists) | Some tray-style apps still slip into lists; users want control | S | None | Alt-Tab Terminator Pro, AltTab (macOS), KDE |
| 6 | **Virtual desktops: current only, or all** | Windows, GNOME and KDE all offer this | S | None | Windows, GNOME, KDE |
| 7 | **Each monitor lists only its own windows** (optional) | The multi-monitor feature people ask for most. KDE calls it "current screen only" | M | None | KDE, DisplayFusion, VistaSwitcher, Windhawk mod |
| 8 | **"Show on" option: the monitor with the active window** | Completes the existing all / under-mouse choice | S | None | VistaSwitcher, AltTab (macOS) |
| 9 | **Licence**: MIT | Open source by default: without a licence nobody may legally use the code | S | — | — |

## Phase 2: Find windows faster (done)

| # | Feature | Why | Effort | Idle cost | Seen in |
|---|---|---|---|---|---|
| 10 | **Type to search.** Fuzzy match on title and app name; tiles filter as you type | The biggest speed-up with 15+ windows | M | None | Alt-Tab Terminator, Switcheroo, PowerToys, GNOME AATWS |
| 11 | **Multiple shortcuts, each with its own filters.** For example, Alt+Tab = this monitor's windows, Win+Tab = every window, Alt+` = this app's windows. Each can keep or replace Windows' own shortcut | Linux desktops' best idea: different switchers for different jobs, without a settings round-trip | M | None | KDE (main/alternative switchers), GNOME, AltTab (macOS) |
| 12 | **Number keys 1–9** jump to that tile | Fast and predictable for the first row | S | None | Witch, Contexts |
| 13 | **Home / End / Page Up / Page Down** | Navigating long, scrolling lists | S | None | Ours |
| 14 | **Hover to select** (optional) | Some people steer with the mouse | S | None | AltTab (macOS), KDE |

## Phase 3: Looks and settings (done)

| # | Feature | Why | Effort | Idle cost | Seen in |
|---|---|---|---|---|---|
| 15 | ✅ **Settings window**: its own process, started when opened, gone when closed | Editing JSON is a barrier for most people | M–L | None (if freed) | All alternatives |
| 16 | ✅ **Own app and tray icon** | Currently the generic Windows icon | S | None | — |
| 17 | ✅ **Follow Windows light/dark and the accent colour** (or pick colours) | Matches the OS | S–M | None | Alt-Tab Terminator, AltTab (macOS) |
| 18 | ✅ **Layout styles**: thumbnail grid (today), large icons, compact list, icons only | KDE ships several layouts; compact ones scan faster and are the lightest (no live previews) | M | None (saves memory per open) | KDE, Alt-Tab Terminator, AltTab (macOS) |
| 19 | ✅ **Proper icons for Store apps** (from the app package's logo) | Store apps can show a generic icon today | M | None | Windows |
| 20 | ✅ **State badges**: minimized, running as admin, not responding, on another desktop | Explains surprises | S–M | None | Ours |
| 21 | ✅ **Peek**: highlight the selected window where it really is | On two screens, see which window is about to come forward | M | None | Windows (Aero Peek), KDE ("show selected window"), AltTab (macOS) |
| 22 | ✅ **"Show desktop" tile** (optional) | Quick way to clear the screen from the switcher | S | None | KDE |
| 23 | ✅ **Accessibility**: screen-reader names, high contrast, keyboard-only | Needed before a public release | M | None | Windows, GNOME |

## Phase 4: Multi-monitor power tools (done)

| # | Feature | Why | Effort | Idle cost | Seen in |
|---|---|---|---|---|---|
| 24 | ✅ **Move the selected window to this monitor** (a key in the switcher) | Unique to a switcher on every screen: pull a window to where you're looking | S–M | None | Ours |
| 25 | ✅ **Window actions in the switcher**: minimize, maximize, quit app, always-on-top | Saves a trip to the window. macOS lets you quit or hide apps right from Cmd+Tab | S each | None | macOS, Alt-Tab Terminator, AltTab (macOS) |
| 26 | ✅ **Sort and group**: recently used (today), stacking order, by app, alphabetical; optional per-monitor sections | KDE offers stacking order as well as recently used | M | None | KDE, AltTab (macOS) |
| 27 | ✅ **Previews for minimized windows** | Removes a known gap. No snapshot needed: DWM keeps a minimized window's last image (for the taskbar), and a normal thumbnail draws it | S | None | Alt-Tab Terminator |

## Phase 5: Public release (done)

| # | Feature | Why | Effort | Idle cost | Seen in |
|---|---|---|---|---|---|
| 28 | ✅ **Contributing guide and issue templates** | Open source by default | S | — | — |
| 29 | ✅ **CI on GitHub Actions**: build and unit tests on every push | Protects the work as it grows | S | — | — |
| 30 | ✅ **Release packaging**: zips (small and self-contained, x64 and Arm64), checksums, winget manifests, draft release on a tag | Easy install. ReadyToRun was measured and left out: no faster to open, bigger download | M | None | — |
| 31 | ✅ **Update check**: opt-in, at most once a day, at startup only | Current without polling | S | None | Alt-Tab Terminator |
| 32 | ✅ **`uiAccess`** so it works while an admin window has focus: signed on the user's own PC by `install.ps1 -AdminWindows` (no certificate to buy) | Closes the biggest remaining gap. Needs a certificate and install under Program Files | M + cert | None | — |
| 33 | ✅ **Screenshots and a short GIF in the README** (from staged sample windows) | People decide in five seconds | S | — | — |

## Phase 6: Explore later

| # | Feature | Why / risk | Effort | Seen in |
|---|---|---|---|---|
| 34 | **Browser tabs as tiles** (Edge first) | Windows shows Edge tabs in Alt+Tab; Chrome needs an extension. Large and fragile | L | Windows |
| 35 | 🚧 **A lighter UI layer**: plain Win32 (GDI, GDI+, DWM) in C#, NativeAOT optional. In progress: colours and icons are framework-free; a Win32 switcher, with tooltips and screen-reader support, is behind `experimentalWin32Switcher` (half the idle memory, twice as fast to open, same self-test). Next: a Win32 message loop and tray so WPF loads only for the settings window, then NativeAOT | Prototype: ~9 MB private and ~28 ms to open, vs ~40 MB and ~80 ms today | L | Ours |
| 37 | **Touchpad gesture** to open the switcher | Windows maps three-finger swipes to its own switcher; needs research | M | Windows, GNOME |
| 38 | **Translations** | After the settings window exists | M | AltTab (macOS), GNOME, KDE |

## Won't do

- **Drop a file on a tile to open it in that app** (was #36). Built and removed: with the admin-window install the app runs above Explorer's integrity level, where Windows blocks dropping files into it, and it wasn't wanted.
- **Telemetry or analytics.** Nothing leaves the machine.
- **Background polling, periodic refresh, or animations on by default.** They break the resource budget.
- **Cloud sync of settings.** The JSON file can be copied.
