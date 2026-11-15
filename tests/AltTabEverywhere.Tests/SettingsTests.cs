using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_round_trip()
    {
        var json = SettingsStore.Serialize(new Settings());
        var settings = SettingsStore.Deserialize(json);

        Assert.Equal(0, settings.ShowDelayMs);
        Assert.Equal(ShowOn.AllMonitors, settings.ShowOn);
        Assert.True(settings.ShowThumbnails);
        Assert.False(settings.StayOpenAfterAltRelease);
    }

    [Fact]
    public void Phase_2_defaults()
    {
        var settings = SettingsStore.Deserialize("{}");
        Assert.True(settings.TypeToSearch);
        Assert.Equal(SearchMode.Fuzzy, settings.SearchMode);
        Assert.Equal(NumberKeys.Switch, settings.NumberKeys);
        Assert.True(settings.ShowTileNumbers);
        Assert.False(settings.HoverSelects);
        Assert.Equal("Alt+Tab", Assert.Single(settings.Shortcuts).Keys);
    }

    [Fact]
    public void Phase_2_settings_parse()
    {
        var settings = SettingsStore.Deserialize("""
            { "typeToSearch": false, "searchMode": "contains", "numberKeys": "select",
              "showTileNumbers": false, "hoverSelects": true, "numberKeysTypo": 1 }
            """);
        Assert.False(settings.TypeToSearch);
        Assert.Equal(SearchMode.Contains, settings.SearchMode);
        Assert.Equal(NumberKeys.Select, settings.NumberKeys);
        Assert.False(settings.ShowTileNumbers);
        Assert.True(settings.HoverSelects);
    }

    [Fact]
    public void Written_file_uses_camelCase_keys_and_string_enums()
    {
        var json = SettingsStore.Serialize(new Settings());
        Assert.Contains("\"showDelayMs\"", json);
        Assert.Contains("\"showOn\": \"allMonitors\"", json);
    }

    [Fact]
    public void Hand_edited_file_with_comments_trailing_commas_and_missing_keys_parses()
    {
        const string json = """
            {
              // only change what you need
              "showOn": "monitorUnderCursor",
              "stayOpenAfterAltRelease": true,
            }
            """;
        var settings = SettingsStore.Deserialize(json);

        Assert.Equal(ShowOn.MonitorUnderCursor, settings.ShowOn);
        Assert.True(settings.StayOpenAfterAltRelease);
        Assert.Equal(240, settings.TileWidth); // missing key keeps its default
    }

    [Fact]
    public void Phase_1_settings_have_safe_defaults()
    {
        var settings = SettingsStore.Deserialize("{}");
        Assert.False(settings.StartWithWindows); // never registers itself without being asked
        Assert.Equal(ListWindowsFrom.AllMonitors, settings.ListWindowsFrom);
        Assert.Equal(VirtualDesktops.Current, settings.VirtualDesktops);
        Assert.Empty(settings.ExcludedApps);
        Assert.Empty(settings.ExcludedTitles);
        Assert.True(settings.DeleteKeyCloses);
        Assert.True(settings.MiddleClickCloses);
        Assert.True(settings.ShowCloseButton);
    }

    [Fact]
    public void Phase_1_settings_parse_from_hand_written_json()
    {
        var settings = SettingsStore.Deserialize("""
            {
              "showOn": "monitorWithActiveWindow",
              "listWindowsFrom": "thisMonitor",
              "virtualDesktops": "all",
              "excludedApps": ["superwhisper.exe", "Microsoft.Teams"],
              "excludedTitles": ["Private browsing"],
              "deleteKeyCloses": false,
              "startWithWindows": true
            }
            """);
        Assert.Equal(ShowOn.MonitorWithActiveWindow, settings.ShowOn);
        Assert.Equal(ListWindowsFrom.ThisMonitor, settings.ListWindowsFrom);
        Assert.Equal(VirtualDesktops.All, settings.VirtualDesktops);
        Assert.Equal(new[] { "superwhisper.exe", "Microsoft.Teams" }, settings.ExcludedApps);
        Assert.Equal(new[] { "Private browsing" }, settings.ExcludedTitles);
        Assert.False(settings.DeleteKeyCloses);
        Assert.True(settings.StartWithWindows);
    }

    [Fact]
    public void Null_lists_become_empty() =>
        Assert.Empty(SettingsStore.Deserialize("""{ "excludedApps": null }""").ExcludedApps);

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var settings = SettingsStore.Deserialize("""{ "showDelayMs": -5, "tileWidth": 5000, "thumbnailHeight": 1 }""");

        Assert.Equal(0, settings.ShowDelayMs);
        Assert.Equal(600, settings.TileWidth);
        Assert.Equal(50, settings.ThumbnailHeight);
    }

    [Fact]
    public void Broken_json_throws_so_the_store_can_keep_the_previous_settings() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SettingsStore.Deserialize("{ \"showOn\": "));
}
