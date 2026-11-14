using System;

namespace AltTabEverywhere;

/// <summary>
/// Grid sizing for the switcher, in device-independent pixels. Pure math (no WPF), so it can be
/// unit-tested.
///
/// Every monitor uses the same column count, so a given tile has the same row and column on every
/// screen and Up/Down means the same thing everywhere. Each monitor then picks its own tile size
/// for that column count.
/// </summary>
internal static class TileLayout
{
    // Tile chrome around the thumbnail: margin 5*2 + padding 6*2 + border 2*2.
    public const double ChromeWidth = 26;
    // Header 24 + header gap 6 + the same 26 of chrome.
    public const double ChromeHeight = 56;
    public const double WindowPadding = 10;

    private const double MaxScale = 1.0;
    private const double MinScale = 0.35;
    private const double ScaleStep = 0.05;

    public static double OuterWidth(Settings s, double scale) => s.TileWidth * scale + ChromeWidth;
    public static double OuterHeight(Settings s, double scale) => s.ThumbnailHeight * scale + ChromeHeight;

    /// <summary>Space for tiles on a monitor: 90% x 85% of its work area, minus window padding.</summary>
    public static (double Width, double Height) Available(double workWidthDip, double workHeightDip) =>
        (workWidthDip * 0.9 - 2 * WindowPadding, workHeightDip * 0.85 - 2 * WindowPadding);

    /// <summary>
    /// The most columns this monitor can show for <paramref name="count"/> tiles while everything
    /// fits on screen: tiles shrink as needed, then columns are as many as fit at that size.
    /// </summary>
    public static int MaxColumns(int count, double availWidth, double availHeight, Settings s)
    {
        if (count <= 0) return 1;
        double scale = FitScale(count, availWidth, availHeight, s, fixedColumns: null);
        return Math.Clamp((int)(availWidth / OuterWidth(s, scale)), 1, count);
    }

    /// <summary>
    /// Largest tile scale at which <paramref name="count"/> tiles fit. With <paramref name="fixedColumns"/>
    /// the grid must be exactly that many columns wide.
    /// </summary>
    public static double FitScale(int count, double availWidth, double availHeight, Settings s, int? fixedColumns)
    {
        count = Math.Max(1, count);
        for (double scale = MaxScale; scale > MinScale + 1e-9; scale -= ScaleStep)
        {
            double outerW = OuterWidth(s, scale);
            int columns = fixedColumns ?? Math.Clamp((int)(availWidth / outerW), 1, count);
            if (columns * outerW > availWidth) continue;
            int rows = (count + columns - 1) / columns;
            if (rows * OuterHeight(s, scale) <= availHeight) return scale;
        }
        return MinScale;
    }
}
