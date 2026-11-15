using System;

namespace AltTabEverywhere;

/// <summary>
/// Grid sizing for the switcher, in device-independent pixels. Pure math (no WPF), so it can be
/// unit-tested.
///
/// The switcher takes at most 75% of a monitor's width and 75% of its height. Tiles shrink a
/// little (down to <see cref="ScrollScale"/>) to avoid scrolling; past that the grid scrolls
/// vertically inside the switcher instead of growing.
///
/// Every monitor uses the same column count, so a given tile has the same row and column on every
/// screen and Up/Down means the same thing everywhere.
/// </summary>
internal static class TileLayout
{
    // Tile chrome around the thumbnail: margin 5*2 + padding 6*2 + border 2*2.
    public const double ChromeWidth = 26;
    // Header 24 + header gap 6 + the same 26 of chrome.
    public const double ChromeHeight = 56;
    public const double WindowPadding = 10;
    /// <summary>Width reserved for the vertical scrollbar (8) plus a small gap.</summary>
    public const double ScrollbarAllowance = 12;

    public const double MaxWidthFraction = 0.75;
    public const double MaxHeightFraction = 0.75;

    private const double MaxScale = 1.0;
    /// <summary>Stop shrinking here and scroll instead (unless even one row doesn't fit across).</summary>
    public const double ScrollScale = 0.75;
    private const double MinScale = 0.35;
    private const double ScaleStep = 0.05;
    private const double Epsilon = 1e-9;

    public static double OuterWidth(Settings s, double scale) => s.TileWidth * scale + ChromeWidth;
    public static double OuterHeight(Settings s, double scale) => s.ThumbnailHeight * scale + ChromeHeight;

    /// <summary>Space for the tile grid on a monitor whose work area is the given size (DIPs).</summary>
    public static (double Width, double Height) Available(double workWidthDip, double workHeightDip) =>
        (workWidthDip * MaxWidthFraction - 2 * WindowPadding - ScrollbarAllowance,
         workHeightDip * MaxHeightFraction - 2 * WindowPadding);

    /// <summary>The most columns this monitor can show for <paramref name="count"/> tiles.</summary>
    public static int MaxColumns(int count, double availWidth, double availHeight, Settings s)
    {
        if (count <= 0) return 1;
        double scale = FitScale(count, availWidth, availHeight, s, fixedColumns: null);
        return Math.Clamp((int)(availWidth / OuterWidth(s, scale)), 1, count);
    }

    /// <summary>
    /// Tile scale for <paramref name="count"/> tiles: the largest scale at which everything fits
    /// without scrolling, but never below <see cref="ScrollScale"/> just to avoid a scrollbar.
    /// With <paramref name="fixedColumns"/> the grid must be exactly that many columns wide.
    /// </summary>
    public static double FitScale(int count, double availWidth, double availHeight, Settings s, int? fixedColumns)
    {
        count = Math.Max(1, count);
        for (double scale = MaxScale; scale > MinScale - Epsilon; scale -= ScaleStep)
        {
            double outerW = OuterWidth(s, scale);
            int columns = fixedColumns ?? Math.Clamp((int)(availWidth / outerW), 1, count);
            if (columns * outerW > availWidth) continue; // a row doesn't fit across yet

            int rows = (count + columns - 1) / columns;
            if (rows * OuterHeight(s, scale) <= availHeight) return scale; // fits, no scrolling
            if (scale <= ScrollScale + Epsilon) return scale;              // shrunk enough: scroll
        }
        return MinScale;
    }

    /// <summary>How many whole rows fit in the visible area (for Page Up / Page Down); at least 1.</summary>
    public static int RowsPerPage(int count, double availWidth, double availHeight, Settings s, int columns)
    {
        double scale = FitScale(count, availWidth, availHeight, s, columns);
        return Math.Max(1, (int)(availHeight / OuterHeight(s, scale)));
    }

    /// <summary>Whether the grid at this scale needs a scrollbar.</summary>
    public static bool NeedsScrolling(int count, int columns, double scale, double availHeight, Settings s) =>
        (count + columns - 1) / Math.Max(1, columns) * OuterHeight(s, scale) > availHeight;
}
