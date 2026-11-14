using System;

namespace AltTabEverywhere;

internal enum ShowOn
{
    /// <summary>Every connected monitor.</summary>
    AllMonitors,
    /// <summary>Only the monitor the mouse is on.</summary>
    MonitorUnderCursor,
}

/// <summary>
/// User settings, stored as JSON in %APPDATA%\AltTabEverywhere\settings.json (see SettingsStore).
/// Property names map to camelCase JSON keys; missing keys keep these defaults.
/// </summary>
internal sealed class Settings
{
    /// <summary>A quick Alt+Tab tap shorter than this switches windows without flashing the UI (like Windows).</summary>
    public int ShowDelayMs { get; set; } = 120;

    public ShowOn ShowOn { get; set; } = ShowOn.AllMonitors;

    /// <summary>Live DWM thumbnails. When false, tiles show the app icon only.</summary>
    public bool ShowThumbnails { get; set; } = true;

    /// <summary>
    /// Once the switcher is visible, keep it open after Alt is released. Pick with Enter, a click,
    /// Tab/arrows to move, Esc to close. A quick Alt+Tab tap still switches immediately.
    /// </summary>
    public bool StayOpenAfterAltRelease { get; set; }

    /// <summary>Tile size in device-independent pixels before any shrink-to-fit.</summary>
    public double TileWidth { get; set; } = 240;

    public double ThumbnailHeight { get; set; } = 140;

    /// <summary>Clamps hand-edited values into ranges the UI can cope with.</summary>
    public Settings Normalized()
    {
        ShowDelayMs = Math.Clamp(ShowDelayMs, 0, 2000);
        TileWidth = Math.Clamp(TileWidth, 80, 600);
        ThumbnailHeight = Math.Clamp(ThumbnailHeight, 50, 400);
        if (!Enum.IsDefined(ShowOn)) ShowOn = ShowOn.AllMonitors;
        return this;
    }
}
