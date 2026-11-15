using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData("Alt+Tab", (int)KeyModifiers.Alt, 0x09, 0)]
    [InlineData("alt + tab", (int)KeyModifiers.Alt, 0x09, 0)]
    [InlineData("Win+Tab", (int)KeyModifiers.Win, 0x09, 0)]
    [InlineData("Ctrl+Alt+Tab", (int)(KeyModifiers.Ctrl | KeyModifiers.Alt), 0x09, 0)]
    [InlineData("Alt+`", (int)KeyModifiers.Alt, 0, ShortcutKeys.AboveTabScanCode)]
    [InlineData("Alt+AboveTab", (int)KeyModifiers.Alt, 0, ShortcutKeys.AboveTabScanCode)]
    [InlineData("Alt+Q", (int)KeyModifiers.Alt, 'Q', 0)]
    [InlineData("Ctrl+Shift+F12", (int)(KeyModifiers.Ctrl | KeyModifiers.Shift), 0x7B, 0)]
    [InlineData("Win+Space", (int)KeyModifiers.Win, 0x20, 0)]
    [InlineData("Alt+1", (int)KeyModifiers.Alt, '1', 0)]
    public void Parses_valid_combinations(string text, int modifiers, int vk, int scan)
    {
        Assert.True(ShortcutKeys.TryParse(text, out var keys));
        Assert.Equal(new ShortcutKeys((KeyModifiers)modifiers, vk, scan), keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tab")]            // no modifier: would take Tab away from every app
    [InlineData("Shift+Tab")]      // Shift alone isn't enough
    [InlineData("Alt")]            // no key
    [InlineData("Alt+Tab+Q")]      // two keys
    [InlineData("Alt+Alt+Tab")]    // repeated modifier
    [InlineData("Alt+Banana")]
    [InlineData("Alt++")]
    [InlineData("Alt+F25")]
    public void Rejects_invalid_combinations(string? text)
    {
        Assert.False(ShortcutKeys.TryParse(text, out _));
    }

    [Fact]
    public void Above_tab_key_matches_by_scan_code_whatever_the_layout()
    {
        ShortcutKeys.TryParse("Alt+`", out var keys);
        Assert.True(keys.IsKey(vk: 0xDC /* VK_OEM_5 on a German layout */, scanCode: 0x29));
        Assert.False(keys.IsKey(vk: 0xC0, scanCode: 0x28));
    }

    [Fact]
    public void Formats_back_to_text()
    {
        ShortcutKeys.TryParse("alt+ctrl+tab", out var keys);
        Assert.Equal("Ctrl+Alt+Tab", keys.ToString());
        ShortcutKeys.TryParse("Alt+`", out keys);
        Assert.Equal("Alt+`", keys.ToString());
    }

    [Fact]
    public void Default_is_a_single_alt_tab_shortcut()
    {
        var valid = new Settings().ValidShortcuts(out var rejected);
        Assert.Empty(rejected);
        var only = Assert.Single(valid);
        Assert.Equal("Alt+Tab", only.Keys.ToString());
        Assert.Equal(WindowScope.All, only.Shortcut.Windows);
    }

    [Fact]
    public void Shortcuts_with_overrides_parse_from_json()
    {
        const string json = """
            {
              "listWindowsFrom": "allMonitors",
              "shortcuts": [
                { "keys": "Alt+Tab", "listWindowsFrom": "thisMonitor" },
                { "keys": "Win+Tab", "virtualDesktops": "all", "stayOpenAfterAltRelease": true },
                { "keys": "Alt+`", "windows": "currentApp" },
              ]
            }
            """;
        var settings = SettingsStore.Deserialize(json);
        var valid = settings.ValidShortcuts(out var rejected);

        Assert.Empty(rejected);
        Assert.Equal(3, valid.Count);

        var altTab = settings.ForShortcut(valid[0].Shortcut);
        Assert.Equal(ListWindowsFrom.ThisMonitor, altTab.ListWindowsFrom);
        Assert.Equal(VirtualDesktops.Current, altTab.VirtualDesktops);

        var winTab = settings.ForShortcut(valid[1].Shortcut);
        Assert.Equal(ListWindowsFrom.AllMonitors, winTab.ListWindowsFrom); // not overridden: global value
        Assert.Equal(VirtualDesktops.All, winTab.VirtualDesktops);
        Assert.True(winTab.StayOpenAfterAltRelease);

        var currentApp = settings.ForShortcut(valid[2].Shortcut);
        Assert.Equal(WindowScope.CurrentApp, currentApp.SessionWindows);

        // Overrides never leak into the global settings.
        Assert.Equal(ListWindowsFrom.AllMonitors, settings.ListWindowsFrom);
        Assert.Equal(WindowScope.All, settings.SessionWindows);
    }

    [Fact]
    public void Current_app_shortcut_never_stacks()
    {
        var settings = new Settings { GroupWindows = GroupWindows.Stacked };
        var copy = settings.ForShortcut(new Shortcut { Keys = "Alt+`", Windows = WindowScope.CurrentApp });
        Assert.Equal(GroupWindows.Separate, copy.GroupWindows);
        Assert.Equal(GroupWindows.Stacked, settings.GroupWindows);
    }

    [Fact]
    public void Invalid_and_duplicate_shortcuts_are_skipped()
    {
        var settings = new Settings
        {
            Shortcuts =
            {
                new Shortcut { Keys = "Tab" },
                new Shortcut { Keys = "Win+Tab" },
                new Shortcut { Keys = "win + TAB" },
            },
        };
        var valid = settings.ValidShortcuts(out var rejected);

        // The default Alt+Tab entry plus Win+Tab; "Tab" is invalid and the second Win+Tab a duplicate.
        Assert.Equal(new[] { "Alt+Tab", "Win+Tab" }, valid.Select(v => v.Keys.ToString()));
        Assert.Equal(2, rejected.Count);
    }

    [Fact]
    public void No_valid_shortcut_falls_back_to_alt_tab()
    {
        var settings = SettingsStore.Deserialize("""{ "shortcuts": [ { "keys": "nope" } ] }""");
        var valid = settings.ValidShortcuts(out var rejected);
        Assert.Equal("Alt+Tab", Assert.Single(valid).Keys.ToString());
        Assert.Single(rejected);
    }

    [Fact]
    public void Empty_shortcut_list_falls_back_to_alt_tab()
    {
        var settings = SettingsStore.Deserialize("""{ "shortcuts": [] }""");
        Assert.Equal("Alt+Tab", Assert.Single(settings.ValidShortcuts(out _)).Keys.ToString());
    }

    [Fact]
    public void Written_defaults_omit_unset_overrides()
    {
        var json = SettingsStore.Serialize(new Settings());
        Assert.Contains("\"keys\": \"Alt+Tab\"", json);
        Assert.DoesNotContain("null", json);
    }
}
