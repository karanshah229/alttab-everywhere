using System;

namespace AltTabEverywhere;

/// <summary>
/// The size of one tile in a layout: its content (preview, icon box or list row) plus the fixed
/// chrome around it (margin, padding, border and, where there is one, the title row).
/// </summary>
/// <param name="Scales">Whether the content shrinks to fit (a list row never does; it scrolls).</param>
/// <param name="SingleColumn">One tile per row whatever the width (the list layout).</param>
internal readonly record struct TileGeometry(
    double ContentWidth, double ContentHeight, double ChromeWidth, double ChromeHeight, bool Scales, bool SingleColumn);

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
    // Grid tile chrome: margin 5*2 + padding 6*2 + border 2*2.
    public const double ChromeWidth = 26;
    // Title row 24 + gap 6 + the same 26 of chrome.
    public const double ChromeHeight = 56;
    // A list row: margin 5*2 + padding 8*2 + border 2*2 across; margin 1*2 + padding 3*2 + border 2*2 + title row 24 down.
    public const double ListChromeWidth = 30;
    public const double ListChromeHeight = 36;
    public const double TitleRowHeight = 24;
    /// <summary>The iconsOnly layout's caption under the grid (the selected window's title).</summary>
    public const double CaptionHeight = 30;
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

    /// <summary>The tile size for the settings' layout.</summary>
    public static TileGeometry Geometry(Settings s) => s.Layout switch
    {
        // A large icon in a box a little wider than a title needs, with the title above it.
        SwitcherLayout.Icons => new(Math.Max(100, s.IconSize * 2.5), s.IconSize * 1.5, ChromeWidth, ChromeHeight, Scales: true, SingleColumn: false),
        SwitcherLayout.List => new(s.ListWidth, 0, ListChromeWidth, ListChromeHeight, Scales: false, SingleColumn: true),
        // Just the icon with some room around it; no title row.
        SwitcherLayout.IconsOnly => new(s.IconSize * 1.5, s.IconSize * 1.5, ChromeWidth, ChromeWidth, Scales: true, SingleColumn: false),
        _ => new(s.TileWidth, s.ThumbnailHeight, ChromeWidth, ChromeHeight, Scales: true, SingleColumn: false),
    };

    public static double OuterWidth(Settings s, double scale)
    {
        var g = Geometry(s);
        return g.ContentWidth * (g.Scales ? scale : 1) + g.ChromeWidth;
    }

    public static double OuterHeight(Settings s, double scale)
    {
        var g = Geometry(s);
        return g.ContentHeight * (g.Scales ? scale : 1) + g.ChromeHeight;
    }

    /// <summary>Space for the tile grid on a monitor whose work area is the given size (DIPs).</summary>
    public static (double Width, double Height) Available(double workWidthDip, double workHeightDip, Settings? s = null) =>
        (workWidthDip * MaxWidthFraction - 2 * WindowPadding - ScrollbarAllowance,
         workHeightDip * MaxHeightFraction - 2 * WindowPadding - (s?.Layout == SwitcherLayout.IconsOnly ? CaptionHeight : 0));

    /// <summary>The most columns this monitor can show for <paramref name="count"/> tiles.</summary>
    public static int MaxColumns(int count, double availWidth, double availHeight, Settings s)
    {
        if (count <= 0 || Geometry(s).SingleColumn) return 1;
        double scale = FitScale(count, availWidth, availHeight, s, fixedColumns: null);
        return Math.Clamp((int)(availWidth / OuterWidth(s, scale)), 1, count);
    }

    /// <summary>
    /// Tile scale for <paramref name="count"/> tiles: the largest scale at which everything fits
    /// without scrolling, but never below <see cref="ScrollScale"/> just to avoid a scrollbar.
    /// With <paramref name="fixedColumns"/> the grid must be exactly that many columns wide.
    /// A layout that doesn't scale (the list) is always 1.
    /// </summary>
    public static double FitScale(int count, double availWidth, double availHeight, Settings s, int? fixedColumns)
    {
        if (!Geometry(s).Scales) return 1.0;
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
