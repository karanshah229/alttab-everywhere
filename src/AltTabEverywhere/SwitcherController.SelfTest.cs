using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// --selftest and --measure: drive the real overlays (on their own threads) and check or time them.
/// </summary>
internal sealed partial class SwitcherController
{
    private static readonly int[] SelfTestCounts = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 13, 17, 23, 31, 47 };

    /// <summary>
    /// --selftest: for several tile counts (some tiles are app stacks), checks on every monitor that
    /// each arrow key lands where the tiles' on-screen positions say it should, that the selection is
    /// scrolled into view, that the switcher stays within its size cap, and that opening a stack
    /// (by key and by resting on it) shows its windows and Back returns.
    /// </summary>
    public void RunSelfTest(Action<bool> done) =>
        // Both switchers, one after the other, until the Win32 one replaces WPF's.
        RunSelfTestPass(win32: false, wpfPassed => RunSelfTestPass(win32: true, win32Passed =>
        {
            _win32ForTest = null;
            done(wpfPassed && win32Passed);
        }));

    private async void RunSelfTestPass(bool win32, Action<bool> done)
    {
        _win32ForTest = win32;
        string engine = win32 ? "Win32" : "WPF";
        Log.Info($"selftest: {engine} switcher");
        int checks = 0, failures = 0;
        string layoutTag = ""; // which layout a failure happened in
        void Fail(string message)
        {
            failures++;
            if (failures <= 60) Log.Info($"selftest FAIL: [{engine}] " + layoutTag + message);
        }

        try
        {
            var real = WindowEnumerator.GetSwitchableWindows();
            if (real.Count == 0)
            {
                Log.Info("selftest: no windows to test with");
                done(false);
                return;
            }

            // Every tile count in every layout: navigation is checked against where tiles really are,
            // so the same checks hold for a grid of previews, of icons, or a one-column list.
            foreach (var (layout, count) in Enum.GetValues<SwitcherLayout>().SelectMany(l => SelfTestCounts.Select(c => (l, c))))
            {
                _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                _settingsOverride.ShowDesktopTile = false; // checked on its own below
                _settingsOverride.Layout = layout;
                _settingsOverride.Peek = PeekMode.Outline;
                _settingsOverride.PeekDelayMs = 0;
                _settingsOverride.ScreenReaderAnnouncements = true;
                _settingsOverride.TileMenu = true;
                _settingsOverride.MoveHereKey = "Shift+Enter";
                _settingsOverride.MinimizeKey = "Ctrl+Down";
                _settingsOverride.MaximizeKey = "Ctrl+Up";
                _settingsOverride.AlwaysOnTopKey = "Ctrl+T";
                _settingsOverride.QuitAppKey = "Ctrl+Shift+Q";
                layoutTag = $"[{layout}] ";
                // Repeat real windows to reach the target count; geometry is all that matters here.
                // Every third tile is a two-window stack so badges and stack cycling are covered too.
                _items = Enumerable.Range(0, count).Select(i => i % 3 == 2
                        ? new SwitcherItem(new[] { real[i % real.Count], real[(i + 1) % real.Count] })
                        : new SwitcherItem(new[] { real[i % real.Count] }))
                    .ToList();
                _active = true;
                _sessionId++;
                _selected = 0;
                _traceEnds = true;
                PlanForTest(_items);
                await ShowUiAsync();
                await Settle();

                var shown = _plan.Select(p => p.Host).ToList();
                string grids = string.Join(", ", shown.Select(o => o.Query(w =>
                    $"{o.Monitor.DeviceName}={ColumnsOf(w.TileRects)} cols{(w.IsScrolling ? " scrolling" : "")}")));
                Log.Info($"selftest: {layoutTag}{count} tiles; {grids}; navigation uses {_focusOverlay?.Monitor.DeviceName}");

                foreach (var overlay in shown)
                {
                    checks++;
                    if (!overlay.Query(w => w.WithinSizeCap()))
                        Fail($"{count} tiles, {overlay.Monitor.DeviceName}: switcher larger than " +
                             $"{TileLayout.MaxWidthFraction:P0} x {TileLayout.MaxHeightFraction:P0} ({overlay.Query(w => w.DescribeForLog())})");

                    // The list is one column; iconsOnly shows the selected window's title underneath.
                    if (layout == SwitcherLayout.List)
                    {
                        checks++;
                        int columns = overlay.Query(w => { w.UpdateLayout(); return ColumnsOf(w.TileRects); });
                        if (columns != 1) Fail($"{count} tiles, {overlay.Monitor.DeviceName}: list has {columns} columns");
                    }
                    if (layout == SwitcherLayout.IconsOnly)
                    {
                        Select(count - 1);
                        checks++;
                        string caption = overlay.Query(w => w.CaptionText);
                        if (caption != _items[count - 1].Shown.Title) Fail($"{count} tiles, {overlay.Monitor.DeviceName}: caption \"{caption}\"");
                        Select(0);
                    }
                }

                // Right-click menu: every switcher offers the seven entries, each with its key.
                foreach (var overlay in shown)
                {
                    var entries = overlay.Query(w => w.TileMenuEntries(0));
                    checks += 2;
                    if (entries.Count != 7) Fail($"{count} tiles, {overlay.Monitor.DeviceName}: tile menu has {entries.Count} entries");
                    if (!entries.Contains("Always on top|Ctrl+T") || !entries.Contains("Move to this monitor|Shift+Enter"))
                        Fail($"{count} tiles, {overlay.Monitor.DeviceName}: tile menu entries {string.Join("; ", entries)}");
                }

                // Screen readers: every tile is a list item named after its window, with its place in
                // the list, and the selected tile has focus on every switcher.
                foreach (int index in new[] { 0, count - 1 }.Distinct())
                {
                    Select(index);
                    var item = _items[index];
                    string want = TileAutomation.NameOf(item, StateBadges.For(item.Shown, Settings));
                    foreach (var overlay in shown)
                    {
                        var a = overlay.Query(w => w.AutomationOf(index));
                        checks += 3;
                        if (a.Name != want) Fail($"{count} tiles, {overlay.Monitor.DeviceName}, tile {index}: screen-reader name differs");
                        if (!a.IsListItem || a.Position != index + 1 || a.Count != count)
                            Fail($"{count} tiles, {overlay.Monitor.DeviceName}, tile {index}: {(a.IsListItem ? "list item" : "not a list item")} {a.Position} of {a.Count}");
                        if (!a.Focused) Fail($"{count} tiles, {overlay.Monitor.DeviceName}, tile {index}: selected tile doesn't have focus");
                    }
                }
                Select(0);

                // Peek: the outline follows the selection, sits under every switcher window, and is
                // there whenever the selected window has a visible place to outline.
                foreach (int index in new[] { 0, count - 1, count / 2 }.Distinct())
                {
                    Select(index);
                    UpdatePeek(); // (Select only moves it when the selection changes)
                    var window = _items[index].Shown;
                    bool outlinable = !window.IsMinimized && !window.OnOtherDesktop && Native.IsWindowVisible(window.Handle) && !Native.IsIconic(window.Handle);
                    IntPtr peeked = _peek?.Target ?? IntPtr.Zero;
                    checks++;
                    if (outlinable && peeked != window.Handle) Fail($"{count} tiles, tile {index}: peek {(peeked == IntPtr.Zero ? "not shown" : "on another window")}");
                    if (!outlinable && peeked != IntPtr.Zero) Fail($"{count} tiles, tile {index}: peek shown for a minimized window");
                    if (peeked != IntPtr.Zero)
                    {
                        checks += 2;
                        if (!_peek!.IsVisible) Fail($"{count} tiles, tile {index}: peek window hidden");
                        if (!_peek.IsBelowAll(shown.Select(o => o.Handle).ToList())) Fail($"{count} tiles, tile {index}: peek drawn over the switcher");
                    }
                }
                Select(0);

                // Stacks: opening one shows a Back tile and its windows on every monitor, with the
                // window the stack's tile showed still selected; Back returns to the stack's tile.
                var mainItems = _items;
                _testItems = mainItems;
                _windows = mainItems.SelectMany(i => i.Windows).ToList();
                for (int index = 0; index < count; index++)
                {
                    var stack = mainItems[index];
                    if (!stack.IsStack) continue;
                    Select(index);
                    OpenStack();
                    checks += 3;
                    if (_openStack != stack) { Fail($"{count} tiles, stack {index}: didn't open"); continue; }
                    if (_items.Count != stack.Count + 1 || !_items[0].IsBack) Fail($"{count} tiles, stack {index}: view has {_items.Count} tiles");
                    if (SelectedIsBack || _items[_selected].Shown.Handle != stack.Shown.Handle) Fail($"{count} tiles, stack {index}: wrong window selected");
                    foreach (var overlay in shown)
                    {
                        var (tiles, back) = overlay.Query(w => (w.TileRects.Count, w.ShownHandle(0)));
                        checks++;
                        if (tiles != stack.Count + 1 || back != IntPtr.Zero)
                            Fail($"{count} tiles, {overlay.Monitor.DeviceName}, stack {index}: {tiles} tiles drawn, first is {(back == IntPtr.Zero ? "Back" : "a window")}");
                    }
                    CloseStack();
                    checks += 2;
                    if (_openStack is not null || _items.Count != count) Fail($"{count} tiles, stack {index}: Back left {_items.Count} tiles");
                    if (_selected != index) Fail($"{count} tiles, stack {index}: Back selected tile {_selected}");
                }

                // Resting: on a stack opens it, on Back goes back, and coming back doesn't reopen it.
                int firstStack = mainItems.FindIndex(i => i.IsStack);
                if (firstStack >= 0 && count > 1)
                {
                    int savedDelay = _stackDelayMs;
                    _stackDelayMs = 60;
                    Select(firstStack == 0 ? 1 : 0);
                    Select(firstStack);
                    await Task.Delay(400);
                    checks++;
                    if (_openStack is null) Fail($"{count} tiles: resting on a stack didn't open it");
                    Select(0); // Back
                    await Task.Delay(400);
                    checks += 2;
                    if (_openStack is not null) Fail($"{count} tiles: resting on Back didn't go back");
                    if (_selected != firstStack) Fail($"{count} tiles: Back selected tile {_selected}, not the stack");
                    await Task.Delay(400);
                    checks++;
                    if (_openStack is not null) Fail($"{count} tiles: the stack reopened by itself after Back");
                    _stackDelayMs = savedDelay;
                }
                _testItems = null;
                PlanForTest(mainItems);

                // Home / End / Page Up / Page Down, checked against the tile positions and the
                // number of rows the viewport really shows.
                foreach (var (name, key) in new[] { ("Home", JumpKey.Home), ("End", JumpKey.End), ("PageUp", JumpKey.PageUp), ("PageDown", JumpKey.PageDown) })
                {
                    for (int start = 0; start < count; start++)
                    {
                        Select(start);
                        Jump(key);
                        int selected = _selected;
                        foreach (var overlay in shown)
                        {
                            var (rects, visible, viewport) = overlay.Query(w =>
                            {
                                w.UpdateLayout();
                                return (w.TileRects, w.IsTileFullyVisible(selected), w.ViewportHeight);
                            });
                            // A page is as many rows as the keyboard's switcher shows; mirrors on
                            // smaller screens only have to keep the selection in view.
                            bool paging = key is JumpKey.PageUp or JumpKey.PageDown;
                            if (paging && overlay != _focusOverlay)
                            {
                                checks++;
                                if (!visible) Fail($"{count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: selected tile {selected} not scrolled into view");
                                continue;
                            }
                            checks += 2;
                            int want = key switch
                            {
                                JumpKey.Home => 0,
                                JumpKey.End => count - 1,
                                _ => ExpectedPage(rects, start, key == JumpKey.PageDown ? +1 : -1, viewport),
                            };
                            if (want != selected)
                                Fail($"{count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: got {selected}, screen says {want}");
                            if (!visible)
                                Fail($"{count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: selected tile {selected} not scrolled into view");
                        }
                    }
                }

                foreach (var (name, move, expected) in Directions())
                {
                    for (int start = 0; start < count; start++)
                    {
                        Select(start);
                        move(this);
                        int selected = _selected;
                        foreach (var overlay in shown)
                        {
                            // Queued after the Select above on the overlay's thread, so it sees it.
                            var (rects, visible) = overlay.Query(w =>
                            {
                                w.UpdateLayout();
                                return (w.TileRects, w.IsTileFullyVisible(selected));
                            });
                            checks += 2;
                            int want = expected(rects, start);
                            if (want != selected)
                                Fail($"{count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: got {selected}, screen says {want}");
                            if (!visible)
                                Fail($"{count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: selected tile {selected} not scrolled into view");
                        }
                    }
                }
                End();
                checks++;
                if (_peek is not null) Fail($"{count} tiles: peek outline left behind after the switcher closed");
            }

            // State badges, in every layout: a window in every state shows all four on every monitor,
            // a plain one none, and a badge turned off in settings disappears.
            foreach (var layout in Enum.GetValues<SwitcherLayout>())
            {
                layoutTag = $"[{layout}] ";
                foreach (bool minimizedOn in new[] { true, false })
                {
                    _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                    _settingsOverride.ShowDesktopTile = false; // checked on its own below
                    _settingsOverride.Layout = layout;
                    _settingsOverride.MinimizedBadge = minimizedOn;
                    _settingsOverride.AdminBadge = _settingsOverride.NotRespondingBadge = _settingsOverride.OtherDesktopBadge = true;
                    var troubled = real[0] with { IsMinimized = true, IsElevated = true, IsHung = true, OnOtherDesktop = true, IsTopmost = false };
                    var plain = real[0] with { IsMinimized = false, IsElevated = false, IsHung = false, OnOtherDesktop = false, IsTopmost = false };
                    _items = new List<SwitcherItem> { new(new[] { troubled }), new(new[] { plain }) };
                    _active = true;
                    _sessionId++;
                    _selected = 0;
                    PlanForTest(_items);
                    await ShowUiAsync();
                    await Settle();
                    int want = minimizedOn ? 4 : 3;
                    foreach (var overlay in _plan.Select(p => p.Host))
                    {
                        var (first, second) = overlay.Query(w => (w.StateBadgeLabels(0), w.StateBadgeLabels(1)));
                        checks += 2;
                        if (first.Count != want) Fail($"{overlay.Monitor.DeviceName}: {first.Count} state badges, expected {want} ({string.Join(", ", first)})");
                        if (second.Count != 0) Fail($"{overlay.Monitor.DeviceName}: plain window has {second.Count} state badges");
                    }
                    End();
                }
            }
            layoutTag = "";

            // High contrast, in every layout: tiles land exactly where they do in the normal theme (the
            // thicker border comes out of the margin), and the selection border is the highlight colour.
            foreach (var layout in Enum.GetValues<SwitcherLayout>())
            {
                layoutTag = $"[{layout}] ";
                var positions = new List<List<RectD>>();
                foreach (var contrast in new[] { HighContrastMode.Off, HighContrastMode.On })
                {
                    _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                    _settingsOverride.ShowDesktopTile = false;
                    _settingsOverride.Layout = layout;
                    _settingsOverride.HighContrast = contrast;
                    var theme = Theme.Rebuild(_settingsOverride);
                    _items = Enumerable.Range(0, 7).Select(i => new SwitcherItem(new[] { real[i % real.Count] })).ToList();
                    _active = true;
                    _sessionId++;
                    _selected = 2;
                    PlanForTest(_items);
                    await ShowUiAsync();
                    await Settle();
                    var overlay = _plan[0].Host;
                    var (rects, a) = overlay.Query(w => { w.UpdateLayout(); return (w.TileRects.ToList(), w.AutomationOf(2)); });
                    positions.Add(rects);
                    checks += 2;
                    if (theme.IsHighContrast != (contrast == HighContrastMode.On)) Fail($"high contrast {contrast}: theme says {theme.IsHighContrast}");
                    if (a.Border != theme.SelectionThickness) Fail($"high contrast {contrast}: selection border {a.Border}, expected {theme.SelectionThickness}");
                    End();
                }
                checks++;
                // Each box grows into its margin by the extra border width, so compare centres.
                static (double X, double Y) Centre(RectD r) => (r.X + r.Width / 2, r.Y + r.Height / 2);
                if (!positions[0].Select(Centre).SequenceEqual(positions[1].Select(Centre))) Fail("high contrast moved the tiles");
            }
            Theme.Rebuild(_store.Current);
            layoutTag = "";

            // listWindowsFrom = thisMonitor: each monitor's switcher lists only its own windows.
            _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
            _settingsOverride.ShowDesktopTile = false; // checked on its own below
            _settingsOverride.ListWindowsFrom = ListWindowsFrom.ThisMonitor;
            _settingsOverride.ShowOn = ShowOn.AllMonitors;
            _settingsOverride.ShowDelayMs = 0;
            BeginSession(reverse: false, sendMaskKey: false);
            await WaitUntilShown();
            await Settle();
            var all = _windows.ToList();
            foreach (var entry in _plan)
            {
                var windows = entry.Items.SelectMany(i => i.Windows).ToList();
                bool ownOnly = windows.All(w => w.Monitor == entry.Host.Monitor.Handle);
                bool focusFallback = entry.Host == _focusOverlay && windows.Count == all.Count;
                int tiles = entry.Host.Query(w => w.TileRects.Count);
                checks += 2;
                if (!ownOnly && !focusFallback)
                    Fail($"per-monitor: {entry.Host.Monitor.DeviceName} lists windows from another monitor");
                if (tiles != entry.Items.Count)
                    Fail($"per-monitor: {entry.Host.Monitor.DeviceName} shows {tiles} tiles, expected {entry.Items.Count}");
                Log.Info($"selftest: per-monitor {entry.Host.Monitor.DeviceName}: {entry.Items.Count} tiles{(focusFallback && !ownOnly ? " (focus fallback: all windows)" : "")}");
            }
            int listed = _plan.Sum(p => p.Items.Sum(i => i.Count));
            bool fallbackUsed = _plan.Any(p => p.Host == _focusOverlay && p.Items.Sum(i => i.Count) == all.Count && _plan.Count > 1);
            checks++;
            if (!fallbackUsed && _overlays.Count == _plan.Count && listed != all.Count)
                Fail($"per-monitor: {listed} windows listed across monitors, expected {all.Count}");
            End();

            // Type to search, the "no match" state, and number keys, on a real session.
            _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
            _settingsOverride.ShowDesktopTile = false; // checked on its own below
            _settingsOverride.ListWindowsFrom = ListWindowsFrom.AllMonitors;
            _settingsOverride.ShowOn = ShowOn.AllMonitors;
            _settingsOverride.ShowDelayMs = 0;
            _settingsOverride.NumberKeys = NumberKeys.Select;
            BeginSession(reverse: false, sendMaskKey: false);
            await WaitUntilShown();
            await Settle();
            int everything = _items.Count;
            var queries = new List<(string Label, string Query)> { ("no match", "zqxjkvzqxjkv") };
            // Real words from window titles, so the search has something to find (titles are never logged).
            foreach (string word in _windows.SelectMany(w => w.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                         .Where(t => t.Length >= 3 && t.All(char.IsLetter)).Distinct().Take(4))
                queries.Add(($"a {word.Length}-letter title word", word.ToLowerInvariant()));
            if (queries.Count > 1) queries.Add(("two words", queries[1].Query + " " + queries[1].Query[..2]));

            foreach (var (label, query) in queries)
            {
                // Type it one keystroke at a time, as the hook would.
                SetQuery(new List<string>());
                foreach (char c in query) SetQuery(_searchStrokes.Append(c.ToString()).ToList());
                await Settle();
                int expected = WindowSearch.Filter(Group(_windows), query, Settings.SearchMode, SearchTextOf).Count;
                checks += 2;
                if (_items.Count != expected) Fail($"search ({label}): {_items.Count} tiles, expected {expected}");
                if (_focusOverlay is null || _plan.All(p => p.Host != _focusOverlay)) Fail($"search ({label}): the keyboard's switcher disappeared");
                foreach (var entry in _plan)
                {
                    var (tiles, searchShown, withinCap) = entry.Host.Query(w => (w.TileRects.Count, w.IsSearchBarShown, w.WithinSizeCap()));
                    checks += 3;
                    if (tiles != entry.Items.Count) Fail($"search ({label}), {entry.Host.Monitor.DeviceName}: shows {tiles} tiles, expected {entry.Items.Count}");
                    if (!searchShown) Fail($"search ({label}), {entry.Host.Monitor.DeviceName}: search bar hidden");
                    if (!withinCap) Fail($"search ({label}), {entry.Host.Monitor.DeviceName}: larger than the size cap");
                }
                Log.Info($"selftest: search ({label}): {_items.Count} of {everything} tiles");

                if (_items.Count >= 2)
                {
                    OnNumber(2);
                    checks++;
                    if (_selected != 1) Fail($"search ({label}): number key 2 selected {_selected}");
                }
                OnNumber(_items.Count + 1); // beyond the last tile: ignored
                checks++;
                if (!_active) Fail($"search ({label}): a number past the last tile ended the session");

                // Backspace everything: back to the full list, search bar hidden.
                while (_searchStrokes.Count > 0) SetQuery(_searchStrokes.Take(_searchStrokes.Count - 1).ToList());
                await Settle();
                checks += 2;
                if (_items.Count != everything) Fail($"search ({label}): after clearing, {_items.Count} tiles, expected {everything}");
                if (_plan.Any(p => p.Host.Query(w => w.IsSearchBarShown))) Fail($"search ({label}): search bar still shown after clearing");
            }
            End();

            // Sort orders, on real sessions: tiles come in the chosen order, and the selection still
            // starts on the window used before the current one (or, reversed, the least recent).
            foreach (var order in Enum.GetValues<SortOrder>())
            {
                foreach (bool reverse in new[] { false, true })
                {
                    string tag = $"sort {order}{(reverse ? ", reverse" : "")}";
                    _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                    _settingsOverride.ShowDesktopTile = false;
                    _settingsOverride.SortOrder = order;
                    _settingsOverride.GroupWindows = GroupWindows.Separate;
                    _settingsOverride.ListWindowsFrom = ListWindowsFrom.AllMonitors;
                    _settingsOverride.ShowOn = ShowOn.AllMonitors;
                    _settingsOverride.ShowDelayMs = 0;
                    BeginSession(reverse, sendMaskKey: false);
                    await WaitUntilShown();
                    await Settle();
                    var shownWindows = _items.Select(i => i.Shown).ToList();
                    Func<WindowInfo, IComparable>? key = order switch
                    {
                        SortOrder.Title => w => w.Title.ToUpperInvariant(),
                        SortOrder.App => w => WindowSearch.AppName(w.AppUserModelId, WindowEnumerator.ProcessPathOf(w)).ToUpperInvariant(),
                        SortOrder.Stacking => StackKey(WindowEnumerator.StackingOrder()),
                        SortOrder.Monitor => MonitorKey(),
                        _ => null,
                    };
                    static Func<WindowInfo, IComparable> StackKey(Dictionary<IntPtr, int> z) => w => z.TryGetValue(w.Handle, out int i) ? i : int.MaxValue;
                    static Func<WindowInfo, IComparable> MonitorKey()
                    {
                        var rank = Monitors.GetAll().OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).Select((m, i) => (m.Handle, i)).ToDictionary(p => p.Handle, p => p.i);
                        return w => rank.TryGetValue(w.Monitor, out int r) ? r : int.MaxValue;
                    }
                    if (key is not null)
                    {
                        checks++;
                        // Titles compare with the culture's rules in the app; upper-invariant is close enough except for ties, so only check
                        // orders whose keys are numbers strictly, and titles/apps loosely (non-decreasing under the same comparer).
                        var comparer = order is SortOrder.Title or SortOrder.App
                            ? Comparer<IComparable>.Create((a, b) => string.Compare((string)a, (string)b, StringComparison.CurrentCultureIgnoreCase))
                            : Comparer<IComparable>.Default;
                        for (int i = 1; i < shownWindows.Count; i++)
                            if (comparer.Compare(key(shownWindows[i - 1]), key(shownWindows[i])) > 0) { Fail($"{tag}: tiles {i - 1} and {i} out of order"); break; }
                    }
                    int expected = WindowOrder.StartIndex(_items, _recent, _originalForeground, reverse);
                    var recentOthers = _recent.Where(w => !(_items.FirstOrDefault(i => i.Contains(_originalForeground))?.Contains(w.Handle) ?? false)).ToList();
                    checks += 2;
                    if (_selected != expected) Fail($"{tag}: started on tile {_selected}, expected {expected}");
                    if (recentOthers.Count > 0 && !_items[_selected].Contains((reverse ? recentOthers[^1] : recentOthers[0]).Handle))
                        Fail($"{tag}: the start tile isn't the {(reverse ? "least" : "most")} recent other window");
                    End();
                }
            }

            // Window actions, on a throwaway window of our own shown as the first tile of a live session:
            // always on top keeps the switcher above it, minimize toggles without switching, and
            // the maximize decision and change work.
            {
                var target = TestWindow("AltTab Everywhere actions test", 60, 60);
                try
                {
                    IntPtr hwnd = target.Handle;
                    var info = new WindowInfo(hwnd, "actions test", null, Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), false, null);
                    _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                    _settingsOverride.ShowDesktopTile = false;
                    _settingsOverride.Layout = SwitcherLayout.Thumbnails;
                    _settingsOverride.ShowThumbnails = true;
                    _settingsOverride.MinimizedPreviews = true;
                    _items = new List<SwitcherItem> { new(new[] { info }), new(new[] { real[0] }) };
                    _testItems = _items; // re-plans after an action keep these tiles
                    _windows = _items.SelectMany(i => i.Windows).ToList();
                    var probeItem = _items[0];
                    _active = true;
                    _sessionId++;
                    _selected = 0;
                    PlanForTest(_items);
                    await ShowUiAsync();
                    await Settle();
                    var hosts = _plan.Select(p => p.Host.Handle).ToList();
                    bool SwitcherAbove()
                    {
                        var remaining = new HashSet<IntPtr>(hosts);
                        for (IntPtr w = Native.GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = Native.GetWindow(w, Native.GW_HWNDNEXT))
                        {
                            if (w == hwnd) return remaining.Count == 0;
                            remaining.Remove(w);
                        }
                        return false;
                    }

                    Perform(WindowAction.AlwaysOnTop, probeItem, null);
                    await Settle();
                    checks += 2;
                    if (!WindowActions.IsTopmost(hwnd)) Fail("always on top: not on top");
                    if (!SwitcherAbove()) Fail("always on top: the window covers the switcher");
                    Perform(WindowAction.AlwaysOnTop, probeItem, null);
                    await Settle();
                    checks++;
                    if (WindowActions.IsTopmost(hwnd)) Fail("always on top: still on top after turning it off");

                    IntPtr foreground = Native.GetForegroundWindow();
                    Perform(WindowAction.Minimize, probeItem, null);
                    await Settle();
                    checks++;
                    if (!Native.IsIconic(hwnd)) Fail("minimize: not minimized");
                    // Minimized, it still shows its last image (not just its minimized caption), unless that's turned off.
                    Replan(items => 0);
                    await Settle();
                    var minimizedPreview = _plan[0].Host.Query(w => w.PreviewSourceSize(0));
                    checks++;
                    if (minimizedPreview.Width < 300 || minimizedPreview.Height < 150) Fail($"minimized preview: {minimizedPreview.Width}x{minimizedPreview.Height}, expected its last image");
                    _settingsOverride.MinimizedPreviews = false;
                    Replan(items => 0);
                    await Settle();
                    checks++;
                    if (_plan[0].Host.Query(w => w.PreviewSourceSize(0)) != (0, 0)) Fail("minimized preview shown with minimizedPreviews off");
                    _settingsOverride.MinimizedPreviews = true;
                    Perform(WindowAction.Minimize, probeItem, null);
                    await Settle();
                    checks += 2;
                    if (Native.IsIconic(hwnd)) Fail("minimize again: still minimized");
                    if (Native.GetForegroundWindow() == hwnd && foreground != hwnd) Fail("minimize again: restoring it switched to it");
                    checks++;
                    if (!_active) Fail("minimize / always on top ended the session");
                    End();

                    checks += 3;
                    if (!WindowActions.ShouldMaximize(hwnd)) Fail("maximize: a normal window should be maximized");
                    WindowActions.SetMaximized(hwnd, true);
                    await Settle();
                    if (!IsZoomed(hwnd)) Fail("maximize: not maximized");
                    if (WindowActions.ShouldMaximize(hwnd)) Fail("maximize: a maximized window should be restored next");
                    WindowActions.SetMaximized(hwnd, false);
                    await Settle();
                    checks++;
                    if (IsZoomed(hwnd) || Native.IsIconic(hwnd)) Fail("maximize: not restored");
                }
                finally
                {
                    if (_active) End();
                    target.Dispose();
                }
            }

            // Picking a minimized window (plain Alt+Tab, no move) restores it on the monitor it was
            // minimized on, whichever monitor the switcher is driven from.
            {
                var probe = TestWindow("AltTab Everywhere restore test", 0, 0);
                try
                {
                    IntPtr hwnd = probe.Handle;
                    foreach (var home in Monitors.GetAll())
                    foreach (bool maximized in new[] { false, true })
                    {
                        string tag = $"restore ({home.DeviceName}{(maximized ? ", maximized" : "")})";
                        Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
                        Native.SetWindowPos(hwnd, IntPtr.Zero, home.WorkArea.Left + 60, home.WorkArea.Top + 60, 600, 400, Native.SWP_NOACTIVATE);
                        if (maximized) Native.ShowWindow(hwnd, 3 /* SW_MAXIMIZE */);
                        await Settle();
                        Native.ShowWindow(hwnd, 7 /* SW_SHOWMINNOACTIVE */);
                        await Settle();
                        _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                        _settingsOverride.ShowDesktopTile = false;
                        var info = new WindowInfo(hwnd, "restore test", null, home.Handle, true, null);
                        _items = new List<SwitcherItem> { new(new[] { real[0] }), new(new[] { info }) };
                        _testItems = _items;
                        _windows = _items.SelectMany(i => i.Windows).ToList();
                        _active = true;
                        _sessionId++;
                        _selected = 1;
                        PlanForTest(_items);
                        await ShowUiAsync();
                        await Settle();
                        Commit();
                        await Settle();
                        IntPtr landed = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
                        checks += 2;
                        if (Native.IsIconic(hwnd)) Fail($"{tag}: still minimized after picking it");
                        if (landed != home.Handle) Fail($"{tag}: came back on another monitor");
                    }
                }
                finally
                {
                    if (_active) End();
                    probe.Dispose();
                }
            }

            // Move here: a throwaway window of our own (the user's windows are never touched) moves
            // between every pair of monitors, normal, maximized and minimized, landing inside the
            // target's work area and keeping its state.
            var monitors = Monitors.GetAll();
            if (monitors.Count < 2) Log.Info("selftest: move here skipped (one monitor)");
            else
            {
                var probe = TestWindow("AltTab Everywhere move test", 0, 0);
                try
                {
                    IntPtr hwnd = probe.Handle;
                    foreach (var from in monitors)
                    foreach (var to in monitors.Where(m => m != from))
                    foreach (string state in new[] { "normal", "maximized", "minimized" })
                    {
                        string tag = $"move here ({state}, {from.DeviceName} -> {to.DeviceName})";
                        Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
                        var start = from.WorkArea;
                        Native.SetWindowPos(hwnd, IntPtr.Zero, start.Left + 40, start.Top + 40, 600, 400, Native.SWP_NOACTIVATE);
                        if (state == "maximized") Native.ShowWindow(hwnd, 3 /* SW_MAXIMIZE */);
                        if (state == "minimized") Native.ShowWindow(hwnd, 7 /* SW_SHOWMINNOACTIVE */);
                        await Settle();
                        bool moved = WindowMover.MoveTo(hwnd, to);
                        if (state == "minimized") Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE: restore without focus */);
                        await Settle();
                        Native.GetWindowRect(hwnd, out var rect);
                        IntPtr landed = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
                        checks += 3;
                        if (!moved || landed != to.Handle) Fail($"{tag}: ended on another monitor (moved={moved})");
                        if (state == "maximized" != IsZoomed(hwnd)) Fail($"{tag}: {(IsZoomed(hwnd) ? "maximized" : "not maximized")}");
                        // Maximized windows hang a few invisible pixels outside the work area; allow for it.
                        var area = to.WorkArea;
                        int slack = state == "maximized" ? 16 : 0;
                        if (rect.Left < area.Left - slack || rect.Top < area.Top - slack || rect.Right > area.Right + slack || rect.Bottom > area.Bottom + slack)
                            Fail($"{tag}: {rect} is outside the work area {area}");
                    }
                }
                finally
                {
                    probe.Dispose();
                }
            }

            // Show desktop tile, with every monitor listing all windows and with each listing its own:
            // it comes last on every switcher, has no window, is never where a session starts, can't be
            // closed, gets no peek, and a search keeps it only when it matches. (It's never picked here:
            // that would really clear the screen.)
            foreach (var listFrom in new[] { ListWindowsFrom.AllMonitors, ListWindowsFrom.ThisMonitor })
            {
                foreach (bool reverse in new[] { false, true })
                {
                    string tag = $"desktop tile ({listFrom}{(reverse ? ", reverse" : "")})";
                    _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                    _settingsOverride.ShowDesktopTile = true;
                    _settingsOverride.ListWindowsFrom = listFrom;
                    _settingsOverride.ShowOn = ShowOn.AllMonitors;
                    _settingsOverride.ShowDelayMs = 0;
                    _settingsOverride.Peek = PeekMode.Outline;
                    _settingsOverride.PeekDelayMs = 0;
                    BeginSession(reverse, sendMaskKey: false);
                    await WaitUntilShown();
                    await Settle();
                    foreach (var entry in _plan)
                    {
                        var (tiles, last) = entry.Host.Query(w => (w.TileRects.Count, w.ShownHandle(w.TileRects.Count - 1)));
                        checks += 2;
                        if (entry.Items.Count == 0 || !entry.Items[^1].IsDesktop) Fail($"{tag}, {entry.Host.Monitor.DeviceName}: last tile isn't Show desktop");
                        if (tiles != entry.Items.Count || last != IntPtr.Zero) Fail($"{tag}, {entry.Host.Monitor.DeviceName}: {tiles} tiles drawn, last has a window");
                    }
                    checks++;
                    if (_selected < 0 || _selected >= _items.Count || !_items[_selected].HasWindow) Fail($"{tag}: the session started on tile {_selected}, not a window");

                    Select(_items.Count - 1);
                    int before = _items.Count;
                    CloseSelected();
                    checks += 2;
                    if (!_active || _items.Count != before) Fail($"{tag}: Delete on Show desktop changed the tiles");
                    if (_peek?.Target is { } peeked && peeked != IntPtr.Zero) Fail($"{tag}: Show desktop got a peek outline");

                    if (!reverse && listFrom == ListWindowsFrom.AllMonitors)
                    {
                        foreach (var (query, keeps) in new[] { ("desk", true), ("zqxjkvzqxjkv", false) })
                        {
                            SetQuery(query.Select(c => c.ToString()).ToList());
                            await Settle();
                            checks++;
                            bool kept = _items.Count > 0 && _items[^1].IsDesktop;
                            if (kept != keeps) Fail($"{tag}: search \"{query}\" {(kept ? "kept" : "dropped")} Show desktop");
                        }
                        SetQuery(new List<string>());
                    }
                    End();
                }
            }
            _settingsOverride = null;
        }
        catch (Exception ex)
        {
            Log.Error("selftest", ex);
            failures++;
        }
        finally
        {
            _settingsOverride = null;
        }

        Log.Info($"selftest ({engine} switcher): {checks} checks, {failures} failures");
        done(failures == 0);
    }

    /// <summary>A throwaway top-level window of our own (the user's windows are never touched), shown without focus.</summary>
    private static HiddenWindow TestWindow(string title, int x, int y)
    {
        var window = new HiddenWindow(title, static (uint _, nint _, nint _, out nint result) => { result = 0; return false; },
            Interop.Win32.WS_OVERLAPPEDWINDOW, x, y, 420, 280);
        Native.ShowWindow(window.Handle, 4 /* SW_SHOWNOACTIVATE */);
        return window;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    /// <summary>A shared plan over every monitor for synthetic tiles (self-test).</summary>
    /// <summary>--selftest: synthetic tiles that PlanSession uses instead of the real windows (so stacks can be opened).</summary>
    private List<SwitcherItem>? _testItems;

    private void PlanForTest(List<SwitcherItem> items)
    {
        EnsureOverlays();
        var settings = Settings;
        var hosts = _overlays.ToList();
        int columns = hosts.Min(o => o.MaxColumns(items.Count, settings));
        _plan = hosts.Select(h => new PlanEntry(h, items, columns, MirrorsSelection: true)).ToList();
        _items = items;
        _columns = columns;
        IntPtr cursor = Monitors.UnderCursor();
        _focusOverlay = hosts.FirstOrDefault(h => h.Monitor.Handle == cursor) ?? hosts[0];
    }

    /// <summary>
    /// --measure: open times (first after startup, real flow, stage by stage) and memory and
    /// handle counts across many opens, to catch per-session leaks.
    /// </summary>
    public async void RunMeasure(int sessions, Action done)
    {
        try
        {
            LogResources("measure: start");

            // First Alt+Tab after startup, with the startup warm-up the app does.
            WarmUpThenTrim();
            await Task.Delay(1500);
            LogResources("measure: after warm-up and trim");
            Log.Info($"measure: first Alt+Tab after startup -> on screen: {await TimeOneOpen():0} ms");

            // Cold (overlay windows and threads created on demand) vs warm (reused).
            var cold = new List<double>();
            var warm = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                bool isCold = i % 2 == 0;
                if (isCold) DiscardOverlays();
                (isCold ? cold : warm).Add(await TimeOneOpen());
            }
            Log.Info($"measure: open latency cold {string.Join("/", cold.Select(t => t.ToString("0")))} ms, " +
                     $"warm {string.Join("/", warm.Select(t => t.ToString("0")))} ms");

            // The real flow after the idle trim (the usual case in daily use).
            var visibleAt = new List<double>();
            int cloakedWhilePreparing = 0, shownBeforeCheck = 0, revealedVisible = 0, runs = 0;
            for (int i = 0; i < 7; i++, runs++)
            {
                MemoryTrimmer.TrimNow();
                await Task.Delay(1000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BeginSession(reverse: false, sendMaskKey: false);
                await Task.Delay(5);
                if (_uiShown) shownBeforeCheck++; // built and revealed within 5 ms: nothing to check
                else if (_plan.Count > 0 && _plan.Select(p => p.Host).All(o => IsDwmCloaked(o.Handle))) cloakedWhilePreparing++;
                await WaitUntilShown();
                DwmFlush();
                visibleAt.Add(sw.Elapsed.TotalMilliseconds);
                if (_plan.Count > 0 && _plan.Select(p => p.Host).All(o => !IsDwmCloaked(o.Handle))) revealedVisible++;
                await Settle();
                End();
                _trimmer.Cancel();
            }
            Log.Info($"measure: Alt+Tab -> on screen (median of {runs}, after trim): {Median(visibleAt):0} ms " +
                     $"[{string.Join("/", visibleAt.Select(t => t.ToString("0")))}] with showDelayMs {Settings.ShowDelayMs}; " +
                     $"cloaked while preparing {cloakedWhilePreparing}/{runs - shownBeforeCheck} (shown within 5 ms: {shownBeforeCheck}), uncloaked on reveal {revealedVisible}/{runs}");

            // Stage-by-stage timing of a few opens.
            for (int i = 0; i < 3; i++)
            {
                MemoryTrimmer.TrimNow();
                await Task.Delay(1000);
                _timing = new OpenTiming();
                BeginSession(reverse: false, sendMaskKey: false);
                await WaitUntilShown();
                DwmFlush();
                _timing.Mark("DWM composed");
                Log.Info("measure: stages " + string.Join(", ", _timing.Marks.Select(m => $"{m.Stage} {m.Ms:0}")));
                _timing = null;
                await Settle();
                End();
                _trimmer.Cancel();
            }

            for (int i = 1; i <= sessions; i++)
            {
                BeginSession(reverse: false, sendMaskKey: false);
                await WaitUntilShown();
                await Settle();
                End();
                await Task.Delay(50);
                if (i % 10 == 0) LogResources($"measure: after {i} sessions");
            }

            // The trimmer fires 2 s after the last session closed: this is the steady idle state.
            await Task.Delay(3000);
            LogResources("measure: idle after trim");
            var ui = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "")
                .Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Windows.Forms" or "System.Drawing.Common").ToList();
            Log.Info($"measure: UI frameworks loaded: {(ui.Count == 0 ? "none" : string.Join(", ", ui))}");
        }
        catch (Exception ex)
        {
            Log.Error("measure", ex);
        }
        done();
    }

    /// <summary>Alt+Tab to composed on screen, in ms.</summary>
    private async Task<double> TimeOneOpen()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BeginSession(reverse: false, sendMaskKey: false);
        await WaitUntilShown();
        DwmFlush();
        double ms = sw.Elapsed.TotalMilliseconds;
        await Settle();
        End();
        _trimmer.Cancel();
        return ms;
    }

    private static void LogResources(string label)
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        Log.Info($"{label}: WS {p.WorkingSet64 / 1048576.0:0.0} MB, private {p.PrivateMemorySize64 / 1048576.0:0.0} MB, " +
                 $"managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, handles {p.HandleCount}, threads {p.Threads.Count}, " +
                 $"GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private static bool IsDwmCloaked(IntPtr hwnd) =>
        Interop.Native.DwmGetWindowAttribute(hwnd, Interop.Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static async Task Settle()
    {
        // Let the overlays finish layout, DPI changes and repositioning before reading tile positions.
        await Task.Delay(250);
        await UiThread.Current.YieldIdle();
    }

    private static IEnumerable<(string Name, Action<SwitcherController> Move, Func<IReadOnlyList<RectD>, int, int> Expected)> Directions()
    {
        yield return ("Right", c => c.Move(+1), (r, i) => (i + 1) % r.Count);
        yield return ("Left", c => c.Move(-1), (r, i) => (i - 1 + r.Count) % r.Count);
        yield return ("Down", c => c.MoveRow(+1), ExpectedDown);
        yield return ("Up", c => c.MoveRow(-1), ExpectedUp);
    }

    // ---- What the screen says should happen, worked out from tile positions only ----

    private static bool SameColumn(RectD a, RectD b) => Math.Abs(a.X - b.X) < 2;

    private static int ExpectedDown(IReadOnlyList<RectD> rects, int i)
    {
        var below = Enumerable.Range(0, rects.Count)
            .Where(j => SameColumn(rects[j], rects[i]) && rects[j].Y > rects[i].Y + 2)
            .OrderBy(j => rects[j].Y).ToList();
        if (below.Count > 0) return below[0];

        double lastRowY = rects.Max(r => r.Y);
        if (rects[i].Y < lastRowY - 2) return rects.Count - 1;                 // nothing below: last tile
        return Enumerable.Range(0, rects.Count)                                // bottom row: wrap to top
            .Where(j => SameColumn(rects[j], rects[i])).OrderBy(j => rects[j].Y).First();
    }

    private static int ExpectedUp(IReadOnlyList<RectD> rects, int i)
    {
        var above = Enumerable.Range(0, rects.Count)
            .Where(j => SameColumn(rects[j], rects[i]) && rects[j].Y < rects[i].Y - 2)
            .OrderByDescending(j => rects[j].Y).ToList();
        if (above.Count > 0) return above[0];

        return Enumerable.Range(0, rects.Count)                                // top row: wrap to bottom of column
            .Where(j => SameColumn(rects[j], rects[i])).OrderByDescending(j => rects[j].Y).First();
    }

    /// <summary>
    /// Page Up / Down from tile positions: as many rows as fit in the viewport, same column,
    /// stopping at the first / last row (the last tile when the short last row lacks the column).
    /// </summary>
    private static int ExpectedPage(IReadOnlyList<RectD> rects, int i, int delta, double viewportHeight)
    {
        var rowTops = rects.Select(r => Math.Round(r.Y)).Distinct().OrderBy(y => y).ToList();
        double pitch = rowTops.Count > 1 ? rowTops[1] - rowTops[0] : rects[i].Height + 10;
        int rowsPerPage = Math.Max(1, (int)((viewportHeight + 0.5) / pitch));
        int row = rowTops.IndexOf(Math.Round(rects[i].Y));
        int target = Math.Clamp(row + delta * rowsPerPage, 0, rowTops.Count - 1);
        var hit = Enumerable.Range(0, rects.Count)
            .Where(j => Math.Round(rects[j].Y) == rowTops[target] && SameColumn(rects[j], rects[i])).ToList();
        return hit.Count > 0 ? hit[0] : rects.Count - 1;
    }

    private static int ColumnsOf(IReadOnlyList<RectD> rects) =>
        rects.Count == 0 ? 0 : rects.Count(r => Math.Abs(r.Y - rects[0].Y) < 2);
}
