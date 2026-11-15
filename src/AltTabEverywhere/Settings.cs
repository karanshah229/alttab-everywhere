using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

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

internal enum SearchMode
{
    /// <summary>Letters in order, gaps allowed ("wtm" finds "Windows Terminal").</summary>
    Fuzzy,
    /// <summary>Each typed word must appear as typed (ignoring case).</summary>
    Contains,
}

internal enum NumberKeys
{
    /// <summary>Digits are typed into the search instead.</summary>
    Off,
    /// <summary>1–9 select that tile.</summary>
    Select,
    /// <summary>1–9 switch to that tile's window straight away.</summary>
    Switch,
}

internal enum WindowScope
{
    /// <summary>Every window (subject to the other filters).</summary>
    All,
    /// <summary>Only windows of the app that had focus (like macOS Cmd+` or GNOME Alt+`).</summary>
    CurrentApp,
}

/// <summary>
/// One key combination that opens the switcher, with optional overrides of the global settings
/// for sessions it opens. Unset (null) overrides use the global value.
/// </summary>
internal sealed class Shortcut
{
    /// <summary>"Alt+Tab", "Win+Tab", "Ctrl+Alt+Tab", "Alt+`" (the key above Tab), "Alt+Q", ...</summary>
    public string Keys { get; set; } = "Alt+Tab";

    public WindowScope Windows { get; set; } = WindowScope.All;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShowOn? ShowOn { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ListWindowsFrom? ListWindowsFrom { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VirtualDesktops? VirtualDesktops { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroupWindows? GroupWindows { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StayOpenAfterAltRelease { get; set; }
}

/// <summary>
/// User settings, stored as JSON in %APPDATA%\AltTabEverywhere\settings.json (see SettingsStore).
/// Property names map to camelCase JSON keys; missing keys keep these defaults. Every setting is
/// documented in the README's settings table.
/// </summary>
internal sealed class Settings
{
    // ---- Shortcuts ----

    /// <summary>
    /// Key combinations that open the switcher, each optionally with its own filters. The first
    /// one is also used for the warm-up at startup. Invalid entries are logged and skipped; with
    /// none left, Alt+Tab is used.
    /// </summary>
    public List<Shortcut> Shortcuts { get; set; } = new() { new Shortcut() };

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

    /// <summary>
    /// Stacked mode: resting on a stack this long opens it (its windows as tiles, plus a Back tile;
    /// resting on Back for as long goes back). 0 turns that off; the key above Tab still opens a stack.
    /// </summary>
    public int StackOpenDelayMs { get; set; } = 500;

    /// <summary>Apps never listed: executable names ("notepad.exe" or "notepad"), full paths, or app IDs.</summary>
    public List<string> ExcludedApps { get; set; } = new();

    /// <summary>Windows never listed if their title contains any of these (case-insensitive).</summary>
    public List<string> ExcludedTitles { get; set; } = new();

    // ---- Finding windows ----

    /// <summary>Typing while the switcher is open filters the tiles by window title and app name.</summary>
    public bool TypeToSearch { get; set; } = true;

    public SearchMode SearchMode { get; set; } = SearchMode.Fuzzy;

    /// <summary>What 1–9 do while the switcher is open (before anything has been typed).</summary>
    public NumberKeys NumberKeys { get; set; } = NumberKeys.Switch;

    /// <summary>Show 1–9 on the first nine tiles (when numberKeys is not off).</summary>
    public bool ShowTileNumbers { get; set; } = true;

    /// <summary>Moving the mouse over a tile selects it.</summary>
    public bool HoverSelects { get; set; }

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

    /// <summary>"system" follows Windows' app mode; "dark" or "light" fixes it.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Selection colour: "system" (Windows' accent) or "#RRGGBB".</summary>
    public string AccentColor { get; set; } = "system";

    /// <summary>Optional "#RRGGBB" overriding the theme's background; empty uses the theme.</summary>
    public string BackgroundColor { get; set; } = "";

    /// <summary>Optional "#RRGGBB" overriding the theme's text colour; empty uses the theme.</summary>
    public string TextColor { get; set; } = "";

    // ---- System ----

    /// <summary>Start automatically when you sign in to Windows (per-user, no admin rights).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Clamps hand-edited values into ranges the app can cope with.</summary>
    public Settings Normalized()
    {
        ShowDelayMs = Math.Clamp(ShowDelayMs, 0, 2000);
        StackOpenDelayMs = Math.Clamp(StackOpenDelayMs, 0, 5000);
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
        if (!Enum.IsDefined(SearchMode)) SearchMode = SearchMode.Fuzzy;
        if (!Enum.IsDefined(NumberKeys)) NumberKeys = NumberKeys.Switch;
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        AccentColor ??= "system";
        BackgroundColor ??= "";
        TextColor ??= "";
        Shortcuts ??= new List<Shortcut>();
        Shortcuts.RemoveAll(s => s is null);
        return this;
    }

    /// <summary>
    /// For one session only: which windows the shortcut that opened it lists. Not a file setting
    /// (each shortcut has its own "windows" key).
    /// </summary>
    [JsonIgnore]
    public WindowScope SessionWindows { get; private set; } = WindowScope.All;

    /// <summary>A copy of these settings with a shortcut's overrides applied.</summary>
    public Settings ForShortcut(Shortcut shortcut)
    {
        var copy = (Settings)MemberwiseClone();
        copy.SessionWindows = shortcut.Windows;
        if (shortcut.ShowOn is { } showOn && Enum.IsDefined(showOn)) copy.ShowOn = showOn;
        if (shortcut.ListWindowsFrom is { } list && Enum.IsDefined(list)) copy.ListWindowsFrom = list;
        if (shortcut.VirtualDesktops is { } desktops && Enum.IsDefined(desktops)) copy.VirtualDesktops = desktops;
        if (shortcut.GroupWindows is { } group && Enum.IsDefined(group)) copy.GroupWindows = group;
        if (shortcut.StayOpenAfterAltRelease is { } stayOpen) copy.StayOpenAfterAltRelease = stayOpen;
        // One app's windows are listed one per tile: stacking them would give a single tile.
        if (shortcut.Windows == WindowScope.CurrentApp) copy.GroupWindows = GroupWindows.Separate;
        return copy;
    }

    /// <summary>
    /// The shortcuts whose keys parse, in order, without duplicates. Falls back to Alt+Tab when
    /// none do. <paramref name="rejected"/> lists the entries that were skipped.
    /// </summary>
    public List<(Shortcut Shortcut, ShortcutKeys Keys)> ValidShortcuts(out List<string> rejected)
    {
        rejected = new List<string>();
        var valid = new List<(Shortcut, ShortcutKeys)>();
        foreach (var shortcut in Shortcuts)
        {
            if (!ShortcutKeys.TryParse(shortcut.Keys, out var keys))
                rejected.Add($"\"{shortcut.Keys}\" (not a valid key combination)");
            else if (valid.Any(v => v.Item2 == keys))
                rejected.Add($"\"{shortcut.Keys}\" (listed twice)");
            else
                valid.Add((shortcut, keys));
        }
        if (valid.Count == 0)
        {
            var fallback = new Shortcut();
            ShortcutKeys.TryParse(fallback.Keys, out var keys);
            valid.Add((fallback, keys));
        }
        return valid;
    }
}
