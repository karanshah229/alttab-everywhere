using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class WindowGroupingTests
{
    private static WindowInfo W(int handle, string app) =>
        new(new IntPtr(handle), $"window {handle}", Icon: null, Monitor: IntPtr.Zero, IsMinimized: false, AppUserModelId: app);

    // MRU order: Chrome, VS Code A, Outlook, VS Code B, Chrome profile 2, Outlook Calendar
    private static readonly WindowInfo[] Mru =
    {
        W(1, "Chrome.Profile1"), W(2, "VSCode"), W(3, "Outlook"), W(4, "VSCode"), W(5, "Chrome.Profile2"), W(6, "Outlook"),
    };

    private static List<SwitcherItem> Stacked() =>
        WindowGrouping.Build(Mru, GroupWindows.Stacked, w => w.AppUserModelId!);

    [Fact]
    public void Separate_mode_keeps_one_tile_per_window_in_order()
    {
        var items = WindowGrouping.Build(Mru, GroupWindows.Separate, w => w.AppUserModelId!);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, items.Select(i => (int)i.Shown.Handle));
        Assert.All(items, i => Assert.False(i.IsStack));
    }

    [Fact]
    public void Stacked_mode_orders_apps_by_their_most_recent_window()
    {
        var items = Stacked();
        Assert.Equal(new[] { 1, 2, 3, 5 }, items.Select(i => (int)i.Shown.Handle));
    }

    [Fact]
    public void Windows_inside_a_stack_keep_MRU_order()
    {
        var vscode = Stacked()[1];
        Assert.Equal(new[] { 2, 4 }, vscode.Windows.Select(w => (int)w.Handle));
        Assert.True(vscode.IsStack);
    }

    [Fact]
    public void Different_app_ids_of_the_same_browser_stay_apart()
    {
        var items = Stacked();
        Assert.Equal(2, items.Count(i => i.Shown.AppUserModelId!.StartsWith("Chrome")));
    }

    [Fact]
    public void Cycling_wraps_both_ways_and_changes_the_shown_window()
    {
        var outlook = Stacked()[2];
        outlook.Cycle(+1);
        Assert.Equal(6, (int)outlook.Shown.Handle);
        outlook.Cycle(+1);
        Assert.Equal(3, (int)outlook.Shown.Handle);
        outlook.Cycle(-1);
        Assert.Equal(6, (int)outlook.Shown.Handle);
    }

    [Fact]
    public void Contains_finds_any_window_in_the_stack() =>
        Assert.True(Stacked()[1].Contains(new IntPtr(4)));

    [Fact]
    public void Empty_window_list_gives_no_tiles() =>
        Assert.Empty(WindowGrouping.Build(Array.Empty<WindowInfo>(), GroupWindows.Stacked, w => ""));
}
