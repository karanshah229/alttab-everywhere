using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AltTabEverywhere;

/// <summary>
/// --selftest: drives the real overlays through every tile x every arrow key for several window
/// counts, and checks each move against what the tiles' on-screen positions say should happen —
/// on every monitor, not just the one the navigation logic is based on.
/// </summary>
internal sealed partial class SwitcherController
{
    private static readonly int[] SelfTestCounts = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 13, 17, 23, 31, 47 };

    public async void RunSelfTest(Action<bool> done)
    {
        int checks = 0, failures = 0;
        try
        {
            var real = WindowEnumerator.GetSwitchableWindows();
            if (real.Count == 0)
            {
                Log.Info("selftest: no windows to test with");
                done(false);
                return;
            }

            foreach (int count in SelfTestCounts)
            {
                // Repeat real windows to reach the target count; geometry is all that matters here.
                // Every third tile is a two-window stack so badges and stack cycling are covered too.
                _items = Enumerable.Range(0, count).Select(i => i % 3 == 2
                        ? new SwitcherItem(new[] { real[i % real.Count], real[(i + 1) % real.Count] })
                        : new SwitcherItem(new[] { real[i % real.Count] }))
                    .ToList();
                _active = true;
                _selected = 0;
                ShowUi();
                await Settle();

                var shown = _overlays.Where(o => o.IsVisible).ToList();
                string grids = string.Join(", ", shown.Select(o =>
                    $"{o.Monitor.DeviceName}={ColumnsOf(o.TileRects)} cols{(o.IsScrolling ? " scrolling" : "")}"));
                Log.Info($"selftest: {count} tiles; {grids}; navigation uses {_focusOverlay?.Monitor.DeviceName}");

                foreach (var overlay in shown)
                {
                    checks++;
                    if (overlay.WithinSizeCap()) continue;
                    failures++;
                    Log.Info($"selftest FAIL: {count} tiles, {overlay.Monitor.DeviceName}: switcher larger than {TileLayout.MaxWidthFraction:P0} x {TileLayout.MaxHeightFraction:P0} ({overlay.DescribeForLog()})");
                }

                // Stack cycling: the tile must switch to the stack's next window on every monitor,
                // without moving or resizing (which would break the shared grid).
                for (int index = 0; index < count; index++)
                {
                    if (!_items[index].IsStack) continue;
                    Select(index);
                    UpdateLayoutOf(shown);
                    var before = shown.Select(o => o.TileRects).ToList();
                    CycleStack(+1);
                    UpdateLayoutOf(shown);
                    for (int m = 0; m < shown.Count; m++)
                    {
                        checks += 2;
                        if (shown[m].ShownHandle(index) != _items[index].Shown.Handle)
                        {
                            failures++;
                            Log.Info($"selftest FAIL: {count} tiles, {shown[m].Monitor.DeviceName}, cycle stack {index}: tile shows the wrong window");
                        }
                        if (!before[m].SequenceEqual(shown[m].TileRects))
                        {
                            failures++;
                            Log.Info($"selftest FAIL: {count} tiles, {shown[m].Monitor.DeviceName}, cycle stack {index}: tiles moved");
                        }
                    }
                    CycleStack(-1); // back to the first window
                    checks++;
                    if (_items[index].Current != 0) { failures++; Log.Info($"selftest FAIL: cycle back on stack {index}"); }
                }

                foreach (var (name, move, expected) in Directions())
                {
                    for (int start = 0; start < count; start++)
                    {
                        Select(start);
                        move(this);
                        UpdateLayoutOf(shown);
                        foreach (var overlay in shown)
                        {
                            checks++;
                            int want = expected(overlay.TileRects, start);
                            if (want != _selected)
                            {
                                failures++;
                                if (failures <= 60)
                                    Log.Info($"selftest FAIL: {count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: got {_selected}, screen says {want}");
                            }

                            checks++;
                            if (!overlay.IsTileFullyVisible(_selected))
                            {
                                failures++;
                                if (failures <= 60)
                                    Log.Info($"selftest FAIL: {count} tiles, {overlay.Monitor.DeviceName}, {name} from {start}: selected tile {_selected} not scrolled into view");
                            }
                        }
                    }
                }
                End();
            }
        }
        catch (Exception ex)
        {
            Log.Error("selftest", ex);
            failures++;
        }

        Log.Info($"selftest: {checks} checks, {failures} failures");
        done(failures == 0);
    }

    /// <summary>
    /// --measure: opens and closes the real switcher many times and logs memory and handle counts,
    /// to catch per-session leaks. The GC'd numbers at the end separate garbage from real leaks.
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
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BeginSession(reverse: false, sendMaskKey: false);
                while (!_uiShown && sw.ElapsedMilliseconds < 3000) await Task.Delay(1);
                await Dispatcher.Yield(DispatcherPriority.Background);
                DwmFlush();
                Log.Info($"measure: first Alt+Tab after startup -> on screen: {sw.Elapsed.TotalMilliseconds:0} ms");
                await Settle();
                End();
                _trimmer.Cancel();
            }

            // Open latency: cold (overlay windows created on demand) vs warm (reused).
            var cold = new List<double>();
            var warm = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                bool isCold = i % 2 == 0;
                if (isCold) DiscardOverlays();
                BeginSession(reverse: false, sendMaskKey: false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ShowUi();
                await Dispatcher.Yield(DispatcherPriority.Render); // include the first render pass
                sw.Stop();
                (isCold ? cold : warm).Add(sw.Elapsed.TotalMilliseconds);
                await Settle();
                End();
            }
            Log.Info($"measure: open latency cold {string.Join("/", cold.Select(t => t.ToString("0")))} ms, " +
                     $"warm {string.Join("/", warm.Select(t => t.ToString("0")))} ms");

            // Where an open's time goes, in the two states real use alternates between: right after
            // another open (warm) and after the idle memory trim (the usual case in daily use).
            foreach (bool afterTrim in new[] { false, true })
            {
                var list = new List<double>();
                var show = new List<double>();
                var paint = new List<double>();
                for (int i = 0; i < 7; i++)
                {
                    if (afterTrim)
                    {
                        MemoryTrimmer.TrimNow();
                        await Task.Delay(1000);
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    BeginSession(reverse: false, sendMaskKey: false);
                    _showTimer.Stop(); // measure the work, not the deliberate show delay
                    list.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    ShowUi();
                    show.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    await Dispatcher.Yield(DispatcherPriority.Background); // WPF renders the frame
                    DwmFlush();                                           // DWM composes it on screen
                    paint.Add(sw.Elapsed.TotalMilliseconds);
                    await Settle();
                    End();
                    _trimmer.Cancel();
                    await Task.Delay(100);
                }
                Log.Info($"measure: open breakdown {(afterTrim ? "after trim" : "warm      ")} (median ms): " +
                         $"list windows {Median(list):0}, build+layout {Median(show):0}, render+compose {Median(paint):0}, " +
                         $"total {Median(list) + Median(show) + Median(paint):0} (+ showDelayMs {Settings.ShowDelayMs})");
            }

            // The real flow: Alt+Tab pressed -> overlays built cloaked -> revealed when the delay ends.
            var visibleAt = new List<double>();
            int cloakedWhilePreparing = 0, revealedVisible = 0, runs = 0;
            for (int i = 0; i < 7; i++, runs++)
            {
                MemoryTrimmer.TrimNow();
                await Task.Delay(1000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BeginSession(reverse: false, sendMaskKey: false);
                if (_prepared.Count > 0 && _prepared.All(o => IsDwmCloaked(o.Handle))) cloakedWhilePreparing++;
                while (!_uiShown && sw.ElapsedMilliseconds < 2000) await Task.Delay(1);
                await Dispatcher.Yield(DispatcherPriority.Background);
                DwmFlush();
                visibleAt.Add(sw.Elapsed.TotalMilliseconds);
                if (_prepared.Count > 0 && _prepared.All(o => !IsDwmCloaked(o.Handle))) revealedVisible++;
                await Settle();
                End();
                _trimmer.Cancel();
            }
            // Stage-by-stage timing of a few opens.
            for (int i = 0; i < 3; i++)
            {
                MemoryTrimmer.TrimNow();
                await Task.Delay(1000);
                _timing = new OpenTiming();
                System.Windows.Media.CompositionTarget.Rendering += OnRendering;
                BeginSession(reverse: false, sendMaskKey: false);
                _timing.Mark("BeginSession returned");
                while (!_uiShown && _timing.Marks[^1].Ms < 3000) await Task.Delay(1);
                await Dispatcher.Yield(DispatcherPriority.Background);
                _timing.Mark("yield done");
                DwmFlush();
                _timing.Mark("DWM composed");
                System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
                Log.Info("measure: stages " + string.Join(", ", _timing.Marks.Select(m => $"{m.Stage} {m.Ms:0}")));
                _timing = null;
                await Settle();
                End();
                _trimmer.Cancel();
            }

            Log.Info($"measure: Alt+Tab -> on screen (median of {runs}, after trim): {Median(visibleAt):0} ms " +
                     $"[{string.Join("/", visibleAt.Select(t => t.ToString("0")))}] with showDelayMs {Settings.ShowDelayMs}; " +
                     $"cloaked while preparing {cloakedWhilePreparing}/{runs}, uncloaked on reveal {revealedVisible}/{runs}");

            for (int i = 1; i <= sessions; i++)
            {
                BeginSession(reverse: false, sendMaskKey: false);
                ShowUi();
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

    private static void LogResources(string label)
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        Log.Info($"{label}: WS {p.WorkingSet64 / 1048576.0:0.0} MB, private {p.PrivateMemorySize64 / 1048576.0:0.0} MB, " +
                 $"managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, handles {p.HandleCount}, " +
                 $"GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private int _renderMarks;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_timing is null || _renderMarks++ > 4) return;
        _timing.Mark("WPF frame");
    }

    private static bool IsDwmCloaked(IntPtr hwnd) =>
        Interop.Native.DwmGetWindowAttribute(hwnd, Interop.Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static void UpdateLayoutOf(IEnumerable<SwitcherWindow> overlays)
    {
        foreach (var overlay in overlays) overlay.UpdateLayout();
    }

    private static async Task Settle()
    {
        // Let WPF finish layout, DPI changes and repositioning before reading tile positions.
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

    private static int ColumnsOf(IReadOnlyList<Rect> rects) =>
        rects.Count == 0 ? 0 : rects.Count(r => Math.Abs(r.Y - rects[0].Y) < 2);
}
