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
