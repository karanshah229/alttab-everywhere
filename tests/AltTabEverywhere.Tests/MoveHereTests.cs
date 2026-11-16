using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class MoveHereTests
{
    private static readonly RECT Left = new(0, 0, 1920, 1160);       // 1920x1200 with a 40 px taskbar
    private static readonly RECT Right = new(1920, 0, 3200, 760);    // a smaller monitor to the right

    [Fact]
    public void A_window_keeps_its_size_and_relative_place()
    {
        var moved = WindowMover.MapRect(new RECT(100, 100, 700, 500), Left, new RECT(1920, 0, 3840, 1160));
        Assert.Equal(600, moved.Width);
        Assert.Equal(400, moved.Height);
        Assert.Equal(1920 + 100, moved.Left);
        Assert.Equal(100, moved.Top);
    }

    [Fact]
    public void A_window_too_big_for_the_new_monitor_shrinks_to_fit_inside()
    {
        var moved = WindowMover.MapRect(new RECT(0, 0, 1800, 1100), Left, Right);
        Assert.True(Right.Contains(moved), moved.ToString());
        Assert.Equal(Right.Width, moved.Width);
        Assert.Equal(Right.Height, moved.Height);
    }

    [Fact]
    public void A_window_at_the_edge_stays_entirely_on_the_new_monitor()
    {
        var moved = WindowMover.MapRect(new RECT(1500, 900, 1910, 1150), Left, Right);
        Assert.True(Right.Contains(moved), moved.ToString());
        Assert.Equal(410, moved.Width);
    }

    [Fact]
    public void Monitors_with_negative_coordinates_work()
    {
        var above = new RECT(0, -1080, 1920, 0);
        var moved = WindowMover.MapRect(new RECT(860, 480, 1060, 680), Left, above);
        Assert.True(above.Contains(moved), moved.ToString());
    }

    [Theory]
    [InlineData("Shift+Enter", 0x0D, true, false)]
    [InlineData("ctrl+enter", 0x0D, false, true)]
    [InlineData("Insert", 0x2D, false, false)]
    [InlineData("F2", 0x71, false, false)]
    [InlineData("Shift+M", 0x4D, true, false)]
    public void Move_here_keys_parse(string text, int vk, bool shift, bool ctrl)
    {
        Assert.True(SwitcherKey.TryParse(text, out var key));
        Assert.Equal(vk, key.VirtualKey);
        Assert.Equal(shift, key.Shift);
        Assert.Equal(ctrl, key.Ctrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("Enter")]          // plain Enter already switches
    [InlineData("Shift+Shift+F2")]
    [InlineData("F2+F3")]
    [InlineData("Alt+F2")]         // Alt is the switcher's own key
    [InlineData("Banana")]
    public void Other_text_means_no_key(string text) => Assert.False(SwitcherKey.TryParse(text, out _));

    [Fact]
    public void Defaults_are_Shift_Enter_and_Ctrl_click()
    {
        var settings = new Settings();
        Assert.Equal("Shift+Enter", settings.MoveHereKey);
        Assert.True(settings.CtrlClickMovesHere);
        var loaded = SettingsStore.Deserialize("""{ "moveHereKey": "off", "ctrlClickMovesHere": false }""");
        Assert.Equal("off", loaded.MoveHereKey);
        Assert.False(loaded.CtrlClickMovesHere);
    }
}

public class WindowActionTests
{
    [Theory]
    [InlineData("Ctrl+Down", 0x28, false, true)]
    [InlineData("Ctrl+Up", 0x26, false, true)]
    [InlineData("Ctrl+T", 0x54, false, true)]
    [InlineData("Shift+Delete", 0x2E, true, false)]
    [InlineData("Shift+Left", 0x25, true, false)]
    public void Action_keys_parse(string text, int vk, bool shift, bool ctrl)
    {
        Assert.True(SwitcherKey.TryParse(text, out var key));
        Assert.Equal((vk, shift, ctrl), (key.VirtualKey, key.Shift, key.Ctrl));
    }

    [Theory]
    [InlineData("Down")]    // moves the selection
    [InlineData("Up")]
    [InlineData("Delete")]  // closes the window
    public void Keys_the_switcher_already_uses_need_a_modifier(string text) => Assert.False(SwitcherKey.TryParse(text, out _));

    [Fact]
    public void Defaults()
    {
        var s = new Settings();
        Assert.Equal("Ctrl+Down", s.MinimizeKey);
        Assert.Equal("Ctrl+Up", s.MaximizeKey);
        Assert.Equal("Ctrl+T", s.AlwaysOnTopKey);
        Assert.Equal("Shift+Delete", s.QuitAppKey);
        Assert.True(s.TileMenu);
        Assert.True(s.AlwaysOnTopBadge);
    }

    [Fact]
    public void Only_move_here_and_maximize_switch_windows()
    {
        Assert.True(WindowActions.EndsSession(WindowAction.MoveHere));
        Assert.True(WindowActions.EndsSession(WindowAction.Maximize));
        Assert.False(WindowActions.EndsSession(WindowAction.Minimize));
        Assert.False(WindowActions.EndsSession(WindowAction.AlwaysOnTop));
        Assert.False(WindowActions.EndsSession(WindowAction.QuitApp));
    }

    [Fact]
    public void Menu_commands_share_the_action_values()
    {
        foreach (var action in Enum.GetValues<WindowAction>())
            Assert.Equal(action.ToString(), ((TileMenuCommand)(int)action).ToString());
    }

    [Fact]
    public void Always_on_top_windows_get_a_badge()
    {
        var w = new WindowInfo(IntPtr.Zero, "t", null, IntPtr.Zero, false, null, IsTopmost: true);
        Assert.Contains(StateBadges.AlwaysOnTop, StateBadges.For(w, new Settings()));
        Assert.DoesNotContain(StateBadges.AlwaysOnTop, StateBadges.For(w, new Settings { AlwaysOnTopBadge = false }));
    }
}
