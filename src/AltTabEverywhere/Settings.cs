using System;
using System.Collections.Generic;

namespace AltTabEverywhere;

internal enum ShowOn
{
    /// <summary>Every connected monitor.</summary>
    AllMonitors,
    /// <summary>Only the monitor the mouse is on.</summary>
    MonitorUnderCursor,
    /// <summary>Only the monitor of the window that had focus when Alt+Tab was pressed.</summary>
    MonitorWithActiveWindow,
}

internal enum ListWindowsFrom
{
    /// <summary>Every switcher lists every window.</summary>
    AllMonitors,
    /// <summary>Each monitor's switcher lists only the windows on that monitor.</summary>
    ThisMonitor,
}

internal enum VirtualDesktops
{
    /// <summary>Only windows on the current virtual desktop (Windows' default).</summary>
    Current,
    /// <summary>Windows on every virtual desktop.</summary>
    All,
}

internal enum GroupWindows
{
    /// <summary>One tile per window.</summary>
    Separate,
    /// <summary>One tile per app; Alt+` cycles through that app's windows.</summary>
    Stacked,
}

/// <summary>
/// User settings, stored as JSON in %APPDATA%\AltTabEverywhere\settings.json (see SettingsStore).
/// Property names map to camelCase JSON keys; missing keys keep these defaults. Every setting is
/// documented in the README's settings table.
/// </summary>
internal sealed class Settings
{
    // ---- When and where the switcher appears ----

    /// <summary>
    /// Wait this long after Alt+Tab before showing the switcher, so taps shorter than this switch
    /// without it appearing. 0 (default) shows it as soon as its first frame is drawn.
    /// </summary>
    public int ShowDelayMs { get; set; }

    public ShowOn ShowOn { get; set; } = ShowOn.AllMonitors;

    /// <summary>
    /// Once the switcher is visible, keep it open after Alt is released. Pick with Enter, a click,
    /// Tab/arrows to move, Esc to close. A quick Alt+Tab tap still switches immediately.
    /// </summary>
    public bool StayOpenAfterAltRelease { get; set; }

    // ---- Which windows are listed ----

    public ListWindowsFrom ListWindowsFrom { get; set; } = ListWindowsFrom.AllMonitors;

    public VirtualDesktops VirtualDesktops { get; set; } = VirtualDesktops.Current;

    public GroupWindows GroupWindows { get; set; } = GroupWindows.Separate;

    /// <summary>Apps never listed: executable names ("notepad.exe" or "notepad"), full paths, or app IDs.</summary>
    public List<string> ExcludedApps { get; set; } = new();

    /// <summary>Windows never listed if their title contains any of these (case-insensitive).</summary>
    public List<string> ExcludedTitles { get; set; } = new();

    // ---- Closing windows from the switcher ----

    public bool DeleteKeyCloses { get; set; } = true;

    public bool MiddleClickCloses { get; set; } = true;

    /// <summary>Show a × on the tile under the mouse.</summary>
    public bool ShowCloseButton { get; set; } = true;

    // ---- Appearance ----

    /// <summary>Live DWM thumbnails. When false, tiles show the app icon only.</summary>
    public bool ShowThumbnails { get; set; } = true;

    /// <summary>Tile size in device-independent pixels before any shrink-to-fit.</summary>
    public double TileWidth { get; set; } = 240;

    public double ThumbnailHeight { get; set; } = 140;

    // ---- System ----

    /// <summary>Start automatically when you sign in to Windows (per-user, no admin rights).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Clamps hand-edited values into ranges the app can cope with.</summary>
    public Settings Normalized()
    {
        ShowDelayMs = Math.Clamp(ShowDelayMs, 0, 2000);
        TileWidth = Math.Clamp(TileWidth, 80, 600);
        ThumbnailHeight = Math.Clamp(ThumbnailHeight, 50, 400);
        if (!Enum.IsDefined(ShowOn)) ShowOn = ShowOn.AllMonitors;
        if (!Enum.IsDefined(ListWindowsFrom)) ListWindowsFrom = ListWindowsFrom.AllMonitors;
        if (!Enum.IsDefined(VirtualDesktops)) VirtualDesktops = VirtualDesktops.Current;
        if (!Enum.IsDefined(GroupWindows)) GroupWindows = GroupWindows.Separate;
        ExcludedApps ??= new List<string>();
        ExcludedTitles ??= new List<string>();
        ExcludedApps.RemoveAll(string.IsNullOrWhiteSpace);
        ExcludedTitles.RemoveAll(string.IsNullOrWhiteSpace);
        return this;
    }
}
