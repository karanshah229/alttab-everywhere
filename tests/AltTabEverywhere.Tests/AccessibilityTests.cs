using System.Windows.Media;

namespace AltTabEverywhere.Tests;

public class AccessibilityTests
{
    private static readonly SystemColorSet BlackContrast = new(Colors.Black, Colors.White, Color.FromRgb(0x1A, 0xEB, 0xFF), Colors.Black);
    private static readonly SystemColorSet WhiteContrast = new(Colors.White, Colors.Black, Color.FromRgb(0x37, 0x00, 0x6E), Colors.White);

    private static WindowInfo Window(string title, bool hung = false, bool minimized = false) =>
        new(IntPtr.Zero, title, null, IntPtr.Zero, minimized, null, IsHung: hung);

    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    [Fact]
    public void A_contrast_theme_uses_only_Windows_colours_solidly()
    {
        var theme = Theme.Create(new Settings(), systemLight: false, null, BlackContrast);
        Assert.True(theme.IsHighContrast);
        Assert.True(theme.IsDark);
        Assert.Equal(Colors.Black, ColorOf(theme.Background));
        Assert.Equal(Colors.White, ColorOf(theme.Text));
        Assert.Equal(Colors.White, ColorOf(theme.DimText)); // no see-through text in a contrast theme
        Assert.Equal(BlackContrast.Highlight, ColorOf(theme.SelectedBorder));
        Assert.Equal(BlackContrast.Highlight, theme.AccentColor); // peek outline too
        Assert.Equal(3, theme.SelectionThickness);
    }

    [Fact]
    public void A_contrast_theme_overrides_custom_colours()
    {
        var settings = new Settings { BackgroundColor = "#FF0000", TextColor = "#00FF00", AccentColor = "#0000FF", Theme = AppTheme.Dark };
        var theme = Theme.Create(settings, systemLight: false, null, WhiteContrast);
        Assert.Equal(Colors.White, ColorOf(theme.Background));
        Assert.Equal(Colors.Black, ColorOf(theme.Text));
        Assert.Equal(WhiteContrast.Highlight, ColorOf(theme.SelectedBorder));
        Assert.False(theme.IsDark);
    }

    [Fact]
    public void The_normal_theme_keeps_its_thinner_border()
    {
        var theme = Theme.Create(new Settings(), systemLight: false, null);
        Assert.False(theme.IsHighContrast);
        Assert.Equal(2, theme.SelectionThickness);
    }

    [Fact]
    public void Defaults_follow_Windows_contrast_and_announce_tiles()
    {
        var settings = new Settings();
        Assert.Equal(HighContrastMode.System, settings.HighContrast);
        Assert.True(settings.ScreenReaderAnnouncements);
        var loaded = SettingsStore.Deserialize("""{ "highContrast": "off", "screenReaderAnnouncements": false }""");
        Assert.Equal(HighContrastMode.Off, loaded.HighContrast);
        Assert.False(loaded.ScreenReaderAnnouncements);
    }

    [Fact]
    public void A_tile_is_read_as_its_title()
    {
        var item = new SwitcherItem(new[] { Window("Inbox - Mail") });
        Assert.Equal("Inbox - Mail", TileAutomation.NameOf(item, Array.Empty<StateBadge>()));
    }

    [Fact]
    public void A_stack_says_how_many_windows_and_states_follow()
    {
        var item = new SwitcherItem(new[] { Window("Report.docx", hung: true), Window("Notes.docx") });
        var states = new[] { StateBadges.NotResponding, StateBadges.Minimized };
        Assert.Equal("Report.docx, 2 windows, not responding, minimized", TileAutomation.NameOf(item, states));
    }

    [Fact]
    public void Special_tiles_and_untitled_windows_have_names()
    {
        Assert.Equal("Back to all windows", TileAutomation.NameOf(SwitcherItem.CreateBack(), Array.Empty<StateBadge>()));
        Assert.Equal("Show desktop", TileAutomation.NameOf(SwitcherItem.CreateDesktop(), Array.Empty<StateBadge>()));
        Assert.Equal("Untitled window", TileAutomation.NameOf(new SwitcherItem(new[] { Window("  ") }), Array.Empty<StateBadge>()));
    }
}
