using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

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
    public async void RunSelfTest(Action<bool> done)
    {
        int checks = 0, failures = 0;
        string layoutTag = ""; // which layout a failure happened in
        void Fail(string message)
        {
            failures++;
            if (failures <= 60) Log.Info("selftest FAIL: " + layoutTag + message);
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
                _settingsOverride.Layout = layout;
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
            }

            // State badges, in every layout: a window in every state shows all four on every monitor,
            // a plain one none, and a badge turned off in settings disappears.
            foreach (var layout in Enum.GetValues<SwitcherLayout>())
            {
                layoutTag = $"[{layout}] ";
                foreach (bool minimizedOn in new[] { true, false })
                {
                    _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
                    _settingsOverride.Layout = layout;
                    _settingsOverride.MinimizedBadge = minimizedOn;
                    _settingsOverride.AdminBadge = _settingsOverride.NotRespondingBadge = _settingsOverride.OtherDesktopBadge = true;
                    var troubled = real[0] with { IsMinimized = true, IsElevated = true, IsHung = true, OnOtherDesktop = true };
                    var plain = real[0] with { IsMinimized = false, IsElevated = false, IsHung = false, OnOtherDesktop = false };
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

            // listWindowsFrom = thisMonitor: each monitor's switcher lists only its own windows.
            _settingsOverride = SettingsStore.Deserialize(SettingsStore.Serialize(_store.Current));
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

        Log.Info($"selftest: {checks} checks, {failures} failures");
        done(failures == 0);
    }

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
            int cloakedWhilePreparing = 0, revealedVisible = 0, runs = 0;
            for (int i = 0; i < 7; i++, runs++)
            {
                MemoryTrimmer.TrimNow();
                await Task.Delay(1000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BeginSession(reverse: false, sendMaskKey: false);
                await Task.Delay(5);
                if (!_uiShown && _plan.Count > 0 && _plan.Select(p => p.Host).All(o => IsDwmCloaked(o.Handle))) cloakedWhilePreparing++;
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
                     $"cloaked while preparing {cloakedWhilePreparing}/{runs}, uncloaked on reveal {revealedVisible}/{runs}");

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
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<(string Name, Action<SwitcherController> Move, Func<IReadOnlyList<Rect>, int, int> Expected)> Directions()
    {
        yield return ("Right", c => c.Move(+1), (r, i) => (i + 1) % r.Count);
        yield return ("Left", c => c.Move(-1), (r, i) => (i - 1 + r.Count) % r.Count);
        yield return ("Down", c => c.MoveRow(+1), ExpectedDown);
        yield return ("Up", c => c.MoveRow(-1), ExpectedUp);
    }

    // ---- What the screen says should happen, worked out from tile positions only ----

    private static bool SameColumn(Rect a, Rect b) => Math.Abs(a.X - b.X) < 2;

    private static int ExpectedDown(IReadOnlyList<Rect> rects, int i)
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

    private static int ExpectedUp(IReadOnlyList<Rect> rects, int i)
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
    private static int ExpectedPage(IReadOnlyList<Rect> rects, int i, int delta, double viewportHeight)
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

    private static int ColumnsOf(IReadOnlyList<Rect> rects) =>
        rects.Count == 0 ? 0 : rects.Count(r => Math.Abs(r.Y - rects[0].Y) < 2);
}
