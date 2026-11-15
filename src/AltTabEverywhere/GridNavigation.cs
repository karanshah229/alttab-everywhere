using System;

namespace AltTabEverywhere;

/// <summary>Selection movement in the tile grid. Pure functions, so they're easy to unit-test.</summary>
internal static class GridNavigation
{
    /// <summary>Next / previous tile, wrapping around the ends.</summary>
    public static int Step(int index, int delta, int count)
    {
        if (count <= 0) return 0;
        return ((index + delta) % count + count) % count;
    }

    /// <summary>
    /// One row up (delta &lt; 0) or down (delta &gt; 0) in a grid filled left-to-right, top-to-bottom.
    /// Down from the row above a short last row lands on the last tile; down from the last row
    /// wraps to the same column in the first row; up from the first row wraps to the bottom of
    /// the same column.
    /// </summary>
    public static int Row(int index, int delta, int count, int columns)
    {
        if (count <= 0) return 0;
        columns = Math.Clamp(columns, 1, count);
        index = Math.Clamp(index, 0, count - 1);

        int column = index % columns;
        int lastRow = (count - 1) / columns;
        int row = index / columns;

        if (delta > 0)
        {
            if (index + columns < count) return index + columns;
            return row < lastRow ? count - 1 : column;
        }

        if (delta < 0)
        {
            if (index - columns >= 0) return index - columns;
            // Wrap to the bottom-most tile in the same column (the last row may be too short).
            int bottom = lastRow * columns + column;
            return bottom < count ? bottom : bottom - columns;
        }

        return index;
    }

    /// <summary>
    /// Page Up / Page Down: <paramref name="rowsPerPage"/> rows up or down in the same column,
    /// stopping (not wrapping) at the first and last rows. Down past a short last row lands on the
    /// last tile.
    /// </summary>
    public static int Page(int index, int delta, int count, int columns, int rowsPerPage)
    {
        if (count <= 0) return 0;
        columns = Math.Clamp(columns, 1, count);
        index = Math.Clamp(index, 0, count - 1);
        int lastRow = (count - 1) / columns;
        int row = Math.Clamp(index / columns + Math.Sign(delta) * Math.Max(1, rowsPerPage), 0, lastRow);
        return Math.Min(row * columns + index % columns, count - 1);
    }
}
