using System;
using System.Collections.Generic;
using System.Linq;

namespace AltTabEverywhere;

/// <summary>
/// One tile in the switcher: a single window, or (in stacked mode) all windows of one app in
/// most-recently-used order. <see cref="Current"/> is the window the tile shows and would activate.
/// </summary>
internal sealed class SwitcherItem
{
    public SwitcherItem(IReadOnlyList<WindowInfo> windows)
    {
        if (windows.Count == 0) throw new ArgumentException("An item needs at least one window.", nameof(windows));
        Windows = windows;
    }

    public IReadOnlyList<WindowInfo> Windows { get; }
    public int Current { get; private set; }
    public WindowInfo Shown => Windows[Current];
    public int Count => Windows.Count;
    public bool IsStack => Windows.Count > 1;

    /// <summary>Moves to the next / previous window in the stack, wrapping around.</summary>
    public void Cycle(int delta) => Current = GridNavigation.Step(Current, delta, Count);

    /// <summary>Makes the tile show a given window of the stack (e.g. the one a search matched).</summary>
    public void ShowWindow(int index) => Current = Math.Clamp(index, 0, Count - 1);

    public bool Contains(IntPtr hwnd) => Windows.Any(w => w.Handle == hwnd);
}

internal static class WindowGrouping
{
    /// <summary>
    /// Separate: one item per window. Stacked: one item per app key, ordered by each app's most
    /// recent window; windows inside a stack keep their MRU order.
    /// </summary>
    public static List<SwitcherItem> Build(IReadOnlyList<WindowInfo> windows, GroupWindows mode, Func<WindowInfo, string> appKey)
    {
        if (mode != GroupWindows.Stacked)
            return windows.Select(w => new SwitcherItem(new[] { w })).ToList();

        var order = new List<string>();
        var groups = new Dictionary<string, List<WindowInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            string key = appKey(window);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new List<WindowInfo>();
                order.Add(key);
            }
            group.Add(window);
        }
        return order.Select(key => new SwitcherItem(groups[key])).ToList();
    }
}
