namespace AltTabEverywhere.Tests;

public class UpdateCheckTests
{
    private const string Page = "https://github.com/karanshah229/alttab-everywhere/releases/tag/v0.7.0";

    private static string Release(string tag = "v0.7.0", string url = Page, bool draft = false, bool prerelease = false) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "{{url}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}} }""";

    [Fact]
    public void A_published_release_is_read()
    {
        var update = UpdateCheck.Parse(Release());
        Assert.NotNull(update);
        Assert.Equal("0.7.0", update!.Version);
        Assert.Equal(Page, update.Url);
    }

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(UpdateCheck.Parse(Release(draft: true)));
        Assert.Null(UpdateCheck.Parse(Release(prerelease: true)));
    }

    [Fact]
    public void Only_this_projects_release_pages_are_ever_opened()
    {
        Assert.Null(UpdateCheck.Parse(Release(url: "https://example.com/download")));
        Assert.Null(UpdateCheck.Parse(Release(url: "https://github.com/someone-else/alttab-everywhere/releases/tag/v0.7.0")));
    }

    [Fact]
    public void A_tag_that_isnt_a_version_is_ignored() =>
        Assert.Null(UpdateCheck.Parse(Release(tag: "latest")));

    [Theory]
    [InlineData("0.7.0", "0.5.0", true)]
    [InlineData("0.5.1", "0.5.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.4.9", "0.5.0", false)]
    [InlineData("0.7.0-beta", "0.6.0", true)]
    [InlineData("0.5.0", "0.5.0+abc123", false)]
    [InlineData("nonsense", "0.5.0", false)]
    public void Versions_compare_by_number(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(candidate, current));

    [Fact]
    public void It_is_off_by_default() => Assert.False(new Settings().CheckForUpdates);

    [Fact]
    public void The_app_knows_its_version() => Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
}
