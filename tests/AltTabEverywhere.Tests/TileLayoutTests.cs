using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class TileLayoutTests
{
    private static readonly Settings Defaults = new();

    // Work areas in DIPs: the laptop panel at 125% and the external monitor at 100%.
    private static readonly (double W, double H) Laptop = TileLayout.Available(1860 / 1.25, 1200 / 1.25);
    private static readonly (double W, double H) External = TileLayout.Available(1872, 1200);

    public static TheoryData<int> Counts => new() { 1, 2, 3, 5, 6, 7, 10, 13, 23, 31, 60 };

    [Theory]
    [MemberData(nameof(Counts))]
    public void Shared_column_count_fits_on_every_monitor(int count)
    {
        int columns = Math.Min(
            TileLayout.MaxColumns(count, Laptop.W, Laptop.H, Defaults),
            TileLayout.MaxColumns(count, External.W, External.H, Defaults));

        foreach (var (w, h) in new[] { Laptop, External })
        {
            double scale = TileLayout.FitScale(count, w, h, Defaults, columns);
            int rows = (count + columns - 1) / columns;
            Assert.True(columns * TileLayout.OuterWidth(Defaults, scale) <= w, $"too wide for {count} tiles");
            // At the minimum scale very large counts may overflow; up to 31 windows must fit.
            if (count <= 31)
                Assert.True(rows * TileLayout.OuterHeight(Defaults, scale) <= h, $"too tall for {count} tiles");
        }
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

    [Fact]
    public void Fixed_columns_never_shrinks_tiles_below_the_free_layout()
    {
        // Using fewer columns than a monitor could fit must not make its tiles smaller.
        double free = TileLayout.FitScale(10, External.W, External.H, Defaults, null);
        int laptopColumns = TileLayout.MaxColumns(10, Laptop.W, Laptop.H, Defaults);
        double shared = TileLayout.FitScale(10, External.W, External.H, Defaults, laptopColumns);
        Assert.True(shared >= free - 1e-9);
    }
}
