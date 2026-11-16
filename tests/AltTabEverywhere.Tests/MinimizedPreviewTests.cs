namespace AltTabEverywhere.Tests;

public class MinimizedPreviewTests
{
    [Theory]
    [InlineData(160, 28, true)]    // Windows' minimized caption
    [InlineData(200, 40, true)]    // the same at a higher scale
    [InlineData(586, 393, false)]  // a real last image
    [InlineData(1920, 1080, false)]
    [InlineData(240, 300, false)]  // narrow but tall: a real (small) window
    public void Only_the_minimized_caption_counts_as_no_image(int width, int height, bool caption) =>
        Assert.Equal(caption, SwitcherWindow.IsMinimizedCaption(width, height));

    [Fact]
    public void Minimized_previews_are_on_by_default()
    {
        Assert.True(new Settings().MinimizedPreviews);
        Assert.False(SettingsStore.Deserialize("""{ "minimizedPreviews": false }""").MinimizedPreviews);
    }
}
