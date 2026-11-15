using System.Windows.Media;
using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class ThemeTests
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(1, 1, 1), Color.FromRgb(2, 2, 2), Color.FromRgb(3, 3, 3), Color.FromRgb(4, 4, 4),
        Color.FromRgb(5, 5, 5), Color.FromRgb(6, 6, 6), Color.FromRgb(7, 7, 7), Color.FromRgb(8, 8, 8),
    };

    [Theory]
    [InlineData("#112233", 0xFF, 0x11, 0x22, 0x33)]
    [InlineData("112233", 0xFF, 0x11, 0x22, 0x33)]
    [InlineData(" #80aabbcc ", 0x80, 0xAA, 0xBB, 0xCC)]
    public void Parses_hex_colours(string text, int a, int r, int g, int b)
    {
        Assert.True(Theme.TryParseColor(text, out var c));
        Assert.Equal(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("system")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Anything_else_is_not_a_colour(string? text) => Assert.False(Theme.TryParseColor(text, out _));

    [Fact]
    public void System_theme_follows_windows_app_mode()
    {
        var settings = new Settings();
        Assert.True(Theme.Create(settings, systemLight: false, Palette).IsDark);
        Assert.False(Theme.Create(settings, systemLight: true, Palette).IsDark);
    }

    [Fact]
    public void Fixed_theme_ignores_windows()
    {
        Assert.True(Theme.Create(new Settings { Theme = AppTheme.Dark }, systemLight: true, Palette).IsDark);
        Assert.False(Theme.Create(new Settings { Theme = AppTheme.Light }, systemLight: false, Palette).IsDark);
    }

    [Fact]
    public void System_accent_uses_the_shade_windows_uses_for_that_background()
    {
        Assert.Equal(Palette[1], Theme.AccentFor("system", dark: true, Palette));  // Light2
        Assert.Equal(Palette[4], Theme.AccentFor("system", dark: false, Palette)); // Dark1
        Assert.Equal(Color.FromRgb(0xFF, 0, 0), Theme.AccentFor("#FF0000", dark: true, Palette));
        // No palette in the registry: Windows' default blue.
        Assert.Equal(Color.FromRgb(0x00, 0x78, 0xD4), Theme.AccentFor("system", dark: false, null));
    }

    [Fact]
    public void A_custom_background_decides_light_or_dark()
    {
        var theme = Theme.Create(new Settings { Theme = AppTheme.Dark, BackgroundColor = "#FFFFFF" }, false, Palette);
        Assert.False(theme.IsDark);
        Assert.Equal(Color.FromRgb(0x1A, 0x1A, 0x1A), ((SolidColorBrush)theme.Text).Color);
    }

    [Fact]
    public void Brushes_are_frozen_so_every_overlay_thread_can_use_them()
    {
        var theme = Theme.Create(new Settings(), false, Palette);
        Assert.True(theme.Background.IsFrozen);
        Assert.True(theme.SelectedBorder.IsFrozen);
        Assert.True(theme.DimText.IsFrozen);
    }

    [Fact]
    public void Theme_settings_round_trip_and_normalize()
    {
        var s = SettingsStore.Deserialize("""{ "theme": "light", "accentColor": "#123456", "backgroundColor": null }""");
        Assert.Equal(AppTheme.Light, s.Theme);
        Assert.Equal("#123456", s.AccentColor);
        Assert.Equal("", s.BackgroundColor);
        Assert.Contains("\"theme\": \"system\"", SettingsStore.Serialize(new Settings()));
    }
}
