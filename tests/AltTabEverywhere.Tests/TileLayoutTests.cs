using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class TileLayoutTests
{
    private static readonly Settings Defaults = new();

    // Work areas in DIPs: the laptop panel at 125% and the external monitor at 100%.
    private static readonly (double W, double H) Laptop = TileLayout.Available(1860 / 1.25, 1200 / 1.25);
    private static readonly (double W, double H) External = TileLayout.Available(1872, 1200);

    public static TheoryData<int> Counts => new() { 1, 2, 3, 5, 6, 7, 10, 13, 23, 31, 60 };

    private static int SharedColumns(int count) => Math.Min(
        TileLayout.MaxColumns(count, Laptop.W, Laptop.H, Defaults),
        TileLayout.MaxColumns(count, External.W, External.H, Defaults));

    [Fact]
    public void Available_space_is_capped_at_75_percent_width_and_height()
    {
        var (w, h) = TileLayout.Available(1000, 1000);
        Assert.True(w <= 750);
        Assert.True(h <= 750);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void Shared_column_count_always_fits_across_every_monitor(int count)
    {
        int columns = SharedColumns(count);
        foreach (var (w, h) in new[] { Laptop, External })
        {
            double scale = TileLayout.FitScale(count, w, h, Defaults, columns);
            Assert.True(columns * TileLayout.OuterWidth(Defaults, scale) <= w, $"too wide for {count} tiles");
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void Tiles_either_fit_without_scrolling_or_stop_shrinking_and_scroll(int count)
    {
        int columns = SharedColumns(count);
        foreach (var (w, h) in new[] { Laptop, External })
        {
            double scale = TileLayout.FitScale(count, w, h, Defaults, columns);
            bool scrolls = TileLayout.NeedsScrolling(count, columns, scale, h, Defaults);
            if (scrolls) Assert.True(scale <= TileLayout.ScrollScale + 1e-9, $"{count} tiles scroll at scale {scale}; should have shrunk first");
        }
    }

    [Fact]
    public void Few_windows_use_full_size_tiles_without_scrolling()
    {
        int columns = SharedColumns(3);
        double scale = TileLayout.FitScale(3, External.W, External.H, Defaults, columns);
        Assert.Equal(1.0, scale, 3);
        Assert.False(TileLayout.NeedsScrolling(3, columns, scale, External.H, Defaults));
    }

    [Fact]
    public void Many_windows_scroll_instead_of_shrinking_below_the_threshold()
    {
        int columns = SharedColumns(60);
        double scale = TileLayout.FitScale(60, External.W, External.H, Defaults, columns);
        Assert.True(scale >= TileLayout.ScrollScale - 1e-9, $"shrunk to {scale}");
        Assert.True(TileLayout.NeedsScrolling(60, columns, scale, External.H, Defaults));
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void Columns_never_exceed_the_tile_count(int count) =>
        Assert.InRange(TileLayout.MaxColumns(count, External.W, External.H, Defaults), 1, count);

    [Fact]
    public void Wider_monitor_fits_at_least_as_many_columns()
    {
        Assert.True(TileLayout.MaxColumns(10, External.W, External.H, Defaults) >=
                    TileLayout.MaxColumns(10, Laptop.W, Laptop.H, Defaults));
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void Rows_per_page_fit_in_the_visible_height(int count)
    {
        int columns = SharedColumns(count);
        int rows = TileLayout.RowsPerPage(count, Laptop.W, Laptop.H, Defaults, columns);
        double scale = TileLayout.FitScale(count, Laptop.W, Laptop.H, Defaults, columns);
        Assert.InRange(rows, 1, 100);
        Assert.True(rows == 1 || rows * TileLayout.OuterHeight(Defaults, scale) <= Laptop.H + 1e-6);
        Assert.True((rows + 1) * TileLayout.OuterHeight(Defaults, scale) > Laptop.H);
    }

    private static Settings With(SwitcherLayout layout) => new() { Layout = layout };

    [Theory]
    [MemberData(nameof(Counts))]
    public void The_list_is_one_column_that_never_shrinks(int count)
    {
        var list = With(SwitcherLayout.List);
        Assert.Equal(1, TileLayout.MaxColumns(count, External.W, External.H, list));
        Assert.Equal(1.0, TileLayout.FitScale(count, External.W, External.H, list, 1));
        Assert.Equal(list.ListWidth + TileLayout.ListChromeWidth, TileLayout.OuterWidth(list, 0.5)); // scale ignored
        Assert.Equal(TileLayout.ListChromeHeight, TileLayout.OuterHeight(list, 1));
    }

    [Theory]
    [InlineData((int)SwitcherLayout.Icons)]
    [InlineData((int)SwitcherLayout.IconsOnly)]
    public void Icon_layouts_fit_more_tiles_per_row_than_previews(int layout)
    {
        var icons = With((SwitcherLayout)layout);
        Assert.True(TileLayout.MaxColumns(60, External.W, External.H, icons) > TileLayout.MaxColumns(60, External.W, External.H, Defaults));
        Assert.True(TileLayout.OuterHeight(icons, 1) < TileLayout.OuterHeight(Defaults, 1));
    }

    [Fact]
    public void Icons_only_has_no_title_row_and_reserves_room_for_its_caption()
    {
        var iconsOnly = With(SwitcherLayout.IconsOnly);
        Assert.Equal(iconsOnly.IconSize * 1.5 + TileLayout.ChromeWidth, TileLayout.OuterHeight(iconsOnly, 1));
        var (_, withCaption) = TileLayout.Available(1872, 1200, iconsOnly);
        var (_, without) = TileLayout.Available(1872, 1200, Defaults);
        Assert.Equal(without - TileLayout.CaptionHeight, withCaption);
    }

    [Theory]
    [InlineData((int)SwitcherLayout.Icons)]
    [InlineData((int)SwitcherLayout.IconsOnly)]
    [InlineData((int)SwitcherLayout.List)]
    public void Every_layout_stays_within_the_width_cap(int layout)
    {
        var s = With((SwitcherLayout)layout);
        foreach (int count in new[] { 1, 7, 60 })
        {
            int columns = TileLayout.MaxColumns(count, Laptop.W, Laptop.H, s);
            double scale = TileLayout.FitScale(count, Laptop.W, Laptop.H, s, columns);
            Assert.True(columns * TileLayout.OuterWidth(s, scale) <= Laptop.W, $"{(SwitcherLayout)layout}: {count} tiles too wide");
        }
    }
}
