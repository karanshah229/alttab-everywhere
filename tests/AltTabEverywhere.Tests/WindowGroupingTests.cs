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
    public void An_open_stack_shows_Back_then_each_window_in_MRU_order()
    {
        var view = WindowGrouping.StackView(Stacked()[2], _ => true);
        Assert.True(view[0].IsBack);
        Assert.Empty(view[0].Windows);
        Assert.Equal(new[] { 3, 6 }, view.Skip(1).Select(i => (int)i.Shown.Handle));
        Assert.All(view.Skip(1), i => Assert.False(i.IsStack));
    }

    [Fact]
    public void An_open_stack_drops_windows_no_longer_listed()
    {
        var view = WindowGrouping.StackView(Stacked()[2], w => w.Handle != new IntPtr(3));
        Assert.Equal(new[] { 6 }, view.Skip(1).Select(i => (int)i.Shown.Handle));
        Assert.Single(WindowGrouping.StackView(Stacked()[2], _ => false)); // Back alone: the caller closes it
    }

    [Fact]
    public void Number_keys_skip_the_Back_tile()
    {
        var view = WindowGrouping.StackView(Stacked()[2], _ => true);
        Assert.Equal(0, SwitcherItem.NumberOf(view, 0));        // Back has no number
        Assert.Equal(1, SwitcherItem.NumberOf(view, 1));
        Assert.Equal(1, SwitcherItem.IndexOfNumber(view, 1));   // 1 = first window, not Back
        Assert.Equal(-1, SwitcherItem.IndexOfNumber(view, 3));  // only two windows
    }

    [Fact]
    public void Number_keys_without_Back_count_from_the_first_tile()
    {
        var items = Stacked();
        Assert.Equal(1, SwitcherItem.NumberOf(items, 0));
        Assert.Equal(0, SwitcherItem.IndexOfNumber(items, 1));
        var many = Enumerable.Range(1, 12).Select(i => new SwitcherItem(new[] { W(i, "a") })).ToList();
        Assert.Equal(0, SwitcherItem.NumberOf(many, 9));        // only 1–9
        Assert.Equal(-1, SwitcherItem.IndexOfNumber(many, 0));
    }

    [Fact]
    public void Contains_finds_any_window_in_the_stack() =>
        Assert.True(Stacked()[1].Contains(new IntPtr(4)));

    [Fact]
    public void Empty_window_list_gives_no_tiles() =>
        Assert.Empty(WindowGrouping.Build(Array.Empty<WindowInfo>(), GroupWindows.Stacked, w => ""));
}
