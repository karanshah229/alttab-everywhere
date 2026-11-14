using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class GridNavigationTests
{
    // 10 tiles, 4 columns:
    //  0  1  2  3
    //  4  5  6  7
    //  8  9

    [Theory]
    [InlineData(0, +1, 1)]
    [InlineData(9, +1, 0)]   // wraps forward
    [InlineData(0, -1, 9)]   // wraps backward
    public void Step_wraps_around(int index, int delta, int expected) =>
        Assert.Equal(expected, GridNavigation.Step(index, delta, 10));

    [Theory]
    [InlineData(1, 5)]       // straight down
    [InlineData(5, 9)]       // into the short last row
    [InlineData(6, 9)]       // nothing directly below -> last tile
    [InlineData(9, 1)]       // last row wraps to the same column in the first row
    [InlineData(8, 0)]
    public void Row_down(int index, int expected) =>
        Assert.Equal(expected, GridNavigation.Row(index, +1, 10, 4));

    [Theory]
    [InlineData(5, 1)]       // straight up
    [InlineData(9, 5)]
    [InlineData(1, 9)]       // first row wraps to the bottom of the same column
    [InlineData(3, 7)]       // column 3 has no tile in the last row -> bottom of column 3 is 7
    public void Row_up(int index, int expected) =>
        Assert.Equal(expected, GridNavigation.Row(index, -1, 10, 4));

    [Fact]
    public void Row_with_a_single_row_stays_in_column_order()
    {
        // 3 tiles, all in one row: up/down wrap onto themselves.
        Assert.Equal(1, GridNavigation.Row(1, +1, 3, 5));
        Assert.Equal(1, GridNavigation.Row(1, -1, 3, 5));
    }

    [Fact]
    public void Empty_list_is_safe()
    {
        Assert.Equal(0, GridNavigation.Step(0, 1, 0));
        Assert.Equal(0, GridNavigation.Row(0, 1, 0, 4));
    }
}
