using System;
using System.Collections.Generic;
using System.Linq;

namespace AltTabEverywhere;

/// <summary>How tiles are ordered (sortOrder).</summary>
internal enum SortOrder
{
    /// <summary>Most recently used first, like Windows (the default).</summary>
    Recent,
    /// <summary>Front to back, as the windows are stacked on screen.</summary>
    Stacking,
    /// <summary>By app name, then most recent first within each app.</summary>
    App,
    /// <summary>By window title, A to Z.</summary>
    Title,
    /// <summary>By monitor (left to right, then top to bottom), then most recent first: one section per screen.</summary>
    Monitor,
}

/// <summary>Orders the session's windows and picks where the selection starts. Pure: lookups are passed in.</summary>
internal static class WindowOrder
{
    /// <param name="recent">The windows, most recently used first (as listed).</param>
    /// <param name="zIndex">Stacking: a window's place front to back (smaller is in front); null if not stacking.</param>
    /// <param name="appName">App: a window's app name.</param>
    /// <param name="monitorRank">Monitor: a window's monitor's place, left to right.</param>
    public static List<WindowInfo> Sort(IReadOnlyList<WindowInfo> recent, SortOrder order,
        Func<WindowInfo, int>? zIndex = null, Func<WindowInfo, string>? appName = null, Func<WindowInfo, int>? monitorRank = null)
    {
        // OrderBy is stable: windows that tie keep their most-recent-first order.
        return order switch
        {
            SortOrder.Stacking when zIndex is not null => recent.OrderBy(zIndex).ToList(),
            SortOrder.App when appName is not null => recent.OrderBy(appName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            SortOrder.Title => recent.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            SortOrder.Monitor when monitorRank is not null => recent.OrderBy(monitorRank).ToList(),
            _ => recent.ToList(),
        };
    }

    /// <summary>
    /// Where the selection starts, whatever the order. A tile's age is that of its most recently
    /// used window. Alt+Tab goes to the youngest tile other than the current window's (the window
    /// used before this one); Shift+Alt+Tab to the oldest. With the default order these are the
    /// second and last tiles, as in Windows. Never Back or Show desktop.
    /// </summary>
    public static int StartIndex(IReadOnlyList<SwitcherItem> items, IReadOnlyList<WindowInfo> recent, IntPtr foreground, bool reverse)
    {
        var rank = new Dictionary<IntPtr, int>();
        for (int i = 0; i < recent.Count; i++) rank.TryAdd(recent[i].Handle, i);

        int best = -1, bestAge = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.HasWindow || (foreground != IntPtr.Zero && item.Contains(foreground))) continue;
            int age = item.Windows.Select(w => rank.TryGetValue(w.Handle, out int r) ? r : int.MaxValue).Min();
            if (best < 0 || (reverse ? age > bestAge : age < bestAge))
            {
                best = i;
                bestAge = age;
            }
        }
        if (best >= 0) return best;
        // Nothing else listed (one window, or one stack): stay on the first window tile.
        for (int i = 0; i < items.Count; i++)
            if (items[i].HasWindow) return i;
        return 0;
    }
}
