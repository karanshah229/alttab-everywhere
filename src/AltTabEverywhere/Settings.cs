namespace AltTabEverywhere;

/// <summary>
/// Prototype settings. Hard-coded for now; the roadmap is to load these from a JSON file
/// and expose them in a settings window.
/// </summary>
internal sealed class Settings
{
    /// <summary>A quick Alt+Tab tap shorter than this switches windows without flashing the UI (like Windows).</summary>
    public int ShowDelayMs { get; init; } = 120;

    /// <summary>true: show the switcher on every monitor. false: only on the monitor under the mouse.</summary>
    public bool ShowOnAllMonitors { get; init; } = true;

    /// <summary>Live DWM thumbnails. When false, tiles show the app icon only.</summary>
    public bool ShowThumbnails { get; init; } = true;

    /// <summary>Tile size in device-independent pixels before any shrink-to-fit.</summary>
    public double TileWidth { get; init; } = 240;

    public double ThumbnailHeight { get; init; } = 140;
}
