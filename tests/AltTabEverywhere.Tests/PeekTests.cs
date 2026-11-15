using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class PeekTests
{
    [Fact]
    public void Peek_is_an_outline_by_default_shown_straight_away()
    {
        var settings = new Settings();
        Assert.Equal(PeekMode.Outline, settings.Peek);
        Assert.Equal(0, settings.PeekDelayMs);
        Assert.Equal(4, settings.PeekBorderWidth);
        Assert.Equal("", settings.PeekColor);
    }

    [Fact]
    public void Peek_settings_load_from_json_and_are_clamped()
    {
        var settings = SettingsStore.Deserialize("""{ "peek": "off", "peekDelayMs": 99999, "peekBorderWidth": 0, "peekColor": "#FF8800" }""");
        Assert.Equal(PeekMode.Off, settings.Peek);
        Assert.Equal(5000, settings.PeekDelayMs);
        Assert.Equal(1, settings.PeekBorderWidth);
        Assert.Equal("#FF8800", settings.PeekColor);
    }

    [Theory]
    [InlineData(4, 1.0, false, 4, 8)]
    [InlineData(4, 1.5, false, 6, 12)]
    [InlineData(4, 2.0, true, 8, 0)]   // maximized windows have square corners
    [InlineData(0.2, 1.0, false, 1, 8)] // never thinner than a pixel
    public void The_ring_scales_with_the_monitor(double widthDip, double scale, bool maximized, int thickness, int radius) =>
        Assert.Equal((thickness, radius), PeekOutline.Shape(widthDip, scale, maximized));
}
