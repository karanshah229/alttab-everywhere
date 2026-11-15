using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class DesktopTileTests
{
    private static WindowInfo Window(int handle) => new((IntPtr)handle, $"w{handle}", null, IntPtr.Zero, false, null);

    [Fact]
    public void The_desktop_tile_has_no_window_and_is_not_Back()
    {
        var desktop = SwitcherItem.CreateDesktop();
        Assert.True(desktop.IsDesktop);
        Assert.False(desktop.IsBack);
        Assert.False(desktop.HasWindow);
        Assert.False(desktop.IsStack);
        Assert.False(desktop.Contains(IntPtr.Zero));
    }

    [Fact]
    public void Windows_and_Back_are_not_the_desktop_tile()
    {
        Assert.True(new SwitcherItem(new[] { Window(1) }).HasWindow);
        Assert.False(new SwitcherItem(new[] { Window(1) }).IsDesktop);
        Assert.False(SwitcherItem.CreateBack().IsDesktop);
        Assert.False(SwitcherItem.CreateBack().HasWindow);
    }

    [Fact]
    public void The_desktop_tile_takes_the_next_number_after_the_windows()
    {
        var items = new List<SwitcherItem> { new(new[] { Window(1) }), new(new[] { Window(2) }), SwitcherItem.CreateDesktop() };
        Assert.Equal(3, SwitcherItem.NumberOf(items, 2));
        Assert.Equal(2, SwitcherItem.IndexOfNumber(items, 3));
    }

    [Fact]
    public void It_is_off_by_default_and_loads_from_json()
    {
        Assert.False(new Settings().ShowDesktopTile);
        Assert.True(SettingsStore.Deserialize("""{ "showDesktopTile": true }""").ShowDesktopTile);
    }

    [Theory]
    [InlineData("desk", 0, true)]
    [InlineData("show", 1, true)]
    [InlineData("zqxjk", 0, false)]
    [InlineData("chrome", 1, false)]
    public void A_search_keeps_it_only_when_the_title_matches(string query, int mode, bool matches) => // 0 fuzzy, 1 contains
        Assert.Equal(matches, WindowSearch.Score(SwitcherItem.DesktopTitle, WindowSearch.Terms(query), (SearchMode)mode) >= 0);

    [Fact]
    public void The_shell_desktop_window_counts_as_the_desktop_and_a_null_handle_does_not()
    {
        Assert.False(ShellDesktop.IsDesktopWindow(IntPtr.Zero));
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) Assert.True(ShellDesktop.IsDesktopWindow(progman)); // no shell on a CI server
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
