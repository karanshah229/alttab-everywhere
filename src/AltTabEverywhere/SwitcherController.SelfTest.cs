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
    private static readonly int[] SelfTestCounts = { 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 13, 17, 23, 31 };

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
                _windows = Enumerable.Range(0, count).Select(i => real[i % real.Count]).ToList();
                _active = true;
                _selected = 0;
                ShowUi();
                await Settle();

                var shown = _overlays.Where(o => o.IsVisible).ToList();
                string grids = string.Join(", ", shown.Select(o => $"{o.Monitor.DeviceName}={ColumnsOf(o.TileRects)} cols"));
                Log.Info($"selftest: {count} windows; {grids}; navigation uses {_focusOverlay?.Monitor.DeviceName}");

                foreach (var (name, move, expected) in Directions())
                {
                    for (int start = 0; start < count; start++)
                    {
                        Select(start);
                        move(this);
                        foreach (var overlay in shown)
                        {
                            checks++;
                            int want = expected(overlay.TileRects, start);
                            if (want == _selected) continue;
                            failures++;
                            if (failures <= 60)
                                Log.Info($"selftest FAIL: {count} windows, {overlay.Monitor.DeviceName}, {name} from {start}: got {_selected}, screen says {want}");
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
