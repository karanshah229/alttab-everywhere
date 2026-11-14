using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AltTabEverywhere;

/// <summary>Which windows to list: exclusions and per-monitor selection. Pure logic, unit-tested.</summary>
internal static class WindowFilter
{
    /// <summary>
    /// Removes windows matching <see cref="Settings.ExcludedApps"/> (executable name with or without
    /// ".exe", full path, or app ID) or <see cref="Settings.ExcludedTitles"/> (substring).
    /// <paramref name="processPath"/> is only called when there are app exclusions.
    /// </summary>
    public static List<WindowInfo> ApplyExclusions(IReadOnlyList<WindowInfo> windows, Settings settings, Func<WindowInfo, string?> processPath)
    {
        if (settings.ExcludedApps.Count == 0 && settings.ExcludedTitles.Count == 0) return windows.ToList();

        var apps = new HashSet<string>(settings.ExcludedApps.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
        return windows.Where(w => !IsExcluded(w, apps, settings.ExcludedTitles, processPath)).ToList();
    }

    private static bool IsExcluded(WindowInfo window, HashSet<string> apps, List<string> titles, Func<WindowInfo, string?> processPath)
    {
        foreach (string fragment in titles)
            if (window.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;

        if (apps.Count == 0) return false;
        if (window.AppUserModelId is { } aumid && apps.Contains(aumid)) return true;

        string? path = processPath(window);
        if (path is null) return false;
        return apps.Contains(path) || apps.Contains(Path.GetFileName(path)) || apps.Contains(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>The windows a switcher on <paramref name="monitor"/> should list.</summary>
    public static List<WindowInfo> ForMonitor(IReadOnlyList<WindowInfo> windows, ListWindowsFrom mode, IntPtr monitor) =>
        mode == ListWindowsFrom.ThisMonitor
            ? windows.Where(w => w.Monitor == monitor).ToList()
            : windows.ToList();
}
