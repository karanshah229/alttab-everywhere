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

    private SwitcherItem(bool back)
    {
        Windows = Array.Empty<WindowInfo>();
        IsBack = back;
        IsDesktop = !back;
    }

    /// <summary>The "Back" tile that leads an open stack's view: no window; picking it returns to every window.</summary>
    public static SwitcherItem CreateBack() => new(back: true);

    /// <summary>The "Show desktop" tile (showDesktopTile): no window; picking it shows the desktop.</summary>
    public static SwitcherItem CreateDesktop() => new(back: false);

    /// <summary>The text the Show desktop tile shows and that a search matches.</summary>
    public const string DesktopTitle = "Show desktop";

    public IReadOnlyList<WindowInfo> Windows { get; }
    public int Current { get; private set; }
    /// <summary>The window the tile shows and would activate. Back and Show desktop have none: check <see cref="HasWindow"/> first.</summary>
    public WindowInfo Shown => Windows[Current];
    public int Count => Windows.Count;
    public bool IsStack => Windows.Count > 1;
    public bool IsBack { get; }
    public bool IsDesktop { get; }
    /// <summary>A window's (or stack's) tile, as opposed to Back or Show desktop.</summary>
    public bool HasWindow => Windows.Count > 0;

    /// <summary>Makes the tile show a given window of the stack (e.g. the one a search matched).</summary>
    public void ShowWindow(int index) => Current = Math.Clamp(index, 0, Count - 1);

    public bool Contains(IntPtr hwnd) => Windows.Any(w => w.Handle == hwnd);

    /// <summary>The 1–9 label of tile <paramref name="index"/>, or 0 for none. A Back tile has none and doesn't count.</summary>
    public static int NumberOf(IReadOnlyList<SwitcherItem> items, int index)
    {
        if (index < 0 || index >= items.Count || items[index].IsBack) return 0;
        int number = index + 1 - (items.Count > 0 && items[0].IsBack ? 1 : 0);
        return number <= 9 ? number : 0;
    }

    /// <summary>The tile that number key <paramref name="number"/> means, or -1.</summary>
    public static int IndexOfNumber(IReadOnlyList<SwitcherItem> items, int number)
    {
        if (number < 1 || number > 9) return -1;
        int index = number - 1 + (items.Count > 0 && items[0].IsBack ? 1 : 0);
        return index < items.Count ? index : -1;
    }
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

    /// <summary>
    /// An open stack's view: a Back tile, then one tile per window of the stack that is still
    /// listed (<paramref name="stillListed"/>), in the stack's order. Just the Back tile if none is.
    /// </summary>
    public static List<SwitcherItem> StackView(SwitcherItem stack, Func<WindowInfo, bool> stillListed)
    {
        var view = new List<SwitcherItem> { SwitcherItem.CreateBack() };
        view.AddRange(stack.Windows.Where(stillListed).Select(w => new SwitcherItem(new[] { w })));
        return view;
    }
}
