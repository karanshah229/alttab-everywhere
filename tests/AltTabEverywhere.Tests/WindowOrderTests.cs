namespace AltTabEverywhere.Tests;

public class WindowOrderTests
{
    private static WindowInfo W(int handle, string title, int monitor = 1) =>
        new((IntPtr)handle, title, null, (IntPtr)monitor, false, null);

    // Most recently used first.
    private static readonly WindowInfo[] Recent = { W(1, "mail", 2), W(2, "Code", 1), W(3, "browser", 2), W(4, "Notes", 1) };

    private static List<SwitcherItem> Tiles(IEnumerable<WindowInfo> windows) => windows.Select(w => new SwitcherItem(new[] { w })).ToList();

    [Fact]
    public void Recent_keeps_the_listed_order() =>
        Assert.Equal(new[] { 1, 2, 3, 4 }, WindowOrder.Sort(Recent, SortOrder.Recent).Select(w => (int)w.Handle));

    [Fact]
    public void Title_sorts_A_to_Z_ignoring_case() =>
        Assert.Equal(new[] { "browser", "Code", "mail", "Notes" }, WindowOrder.Sort(Recent, SortOrder.Title).Select(w => w.Title));

    [Fact]
    public void Stacking_sorts_front_to_back_and_unknown_windows_go_last()
    {
        var z = new Dictionary<IntPtr, int> { [(IntPtr)3] = 0, [(IntPtr)1] = 1, [(IntPtr)4] = 2 };
        var sorted = WindowOrder.Sort(Recent, SortOrder.Stacking, zIndex: w => z.TryGetValue(w.Handle, out int i) ? i : int.MaxValue);
        Assert.Equal(new[] { 3, 1, 4, 2 }, sorted.Select(w => (int)w.Handle));
    }

    [Fact]
    public void App_groups_by_name_keeping_recent_order_within_an_app()
    {
        var apps = new Dictionary<IntPtr, string> { [(IntPtr)1] = "Outlook", [(IntPtr)2] = "Code", [(IntPtr)3] = "Outlook", [(IntPtr)4] = "Code" };
        var sorted = WindowOrder.Sort(Recent, SortOrder.App, appName: w => apps[w.Handle]);
        Assert.Equal(new[] { 2, 4, 1, 3 }, sorted.Select(w => (int)w.Handle));
    }

    [Fact]
    public void Monitor_makes_one_section_per_screen_left_to_right()
    {
        var sorted = WindowOrder.Sort(Recent, SortOrder.Monitor, monitorRank: w => (int)w.Monitor == 1 ? 0 : 1);
        Assert.Equal(new[] { 2, 4, 1, 3 }, sorted.Select(w => (int)w.Handle));
    }

    [Fact]
    public void An_order_without_its_lookup_falls_back_to_recent() =>
        Assert.Equal(new[] { 1, 2, 3, 4 }, WindowOrder.Sort(Recent, SortOrder.Stacking).Select(w => (int)w.Handle));

    [Fact]
    public void With_recent_order_Alt_Tab_starts_on_the_second_tile_and_reverse_on_the_last()
    {
        var tiles = Tiles(Recent);
        Assert.Equal(1, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: false));
        Assert.Equal(3, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: true));
    }

    [Fact]
    public void In_any_order_Alt_Tab_starts_on_the_window_used_before_the_current_one()
    {
        var tiles = Tiles(WindowOrder.Sort(Recent, SortOrder.Title)); // browser, Code, mail, Notes
        Assert.Equal(1, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: false)); // Code (2nd most recent)
        Assert.Equal(3, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: true));  // Notes (least recent)
    }

    [Fact]
    public void Without_a_listed_foreground_window_it_starts_on_the_most_recent()
    {
        var tiles = Tiles(WindowOrder.Sort(Recent, SortOrder.Title));
        Assert.Equal(2, WindowOrder.StartIndex(tiles, Recent, IntPtr.Zero, reverse: false)); // mail
    }

    [Fact]
    public void Stacks_count_by_their_newest_window_and_the_current_stack_is_skipped()
    {
        var tiles = new List<SwitcherItem>
        {
            new(new[] { Recent[0], Recent[2] }), // mail + browser (one app, say)
            new(new[] { Recent[1], Recent[3] }), // Code + Notes
        };
        Assert.Equal(1, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: false));
        Assert.Equal(1, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: true));
    }

    [Fact]
    public void It_never_starts_on_Show_desktop_or_Back()
    {
        var tiles = new List<SwitcherItem> { new(new[] { Recent[0] }), SwitcherItem.CreateDesktop() };
        Assert.Equal(0, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: false));
        Assert.Equal(0, WindowOrder.StartIndex(tiles, Recent, (IntPtr)1, reverse: true));
    }

    [Fact]
    public void Sort_order_defaults_to_recent_and_a_shortcut_can_override_it()
    {
        Assert.Equal(SortOrder.Recent, new Settings().SortOrder);
        var settings = SettingsStore.Deserialize("""{ "sortOrder": "title", "shortcuts": [ { "keys": "Alt+Tab", "sortOrder": "monitor" } ] }""");
        Assert.Equal(SortOrder.Title, settings.SortOrder);
        Assert.Equal(SortOrder.Monitor, settings.ForShortcut(settings.Shortcuts[0]).SortOrder);
    }
}
