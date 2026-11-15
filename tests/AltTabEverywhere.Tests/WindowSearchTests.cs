using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class WindowSearchTests
{
    private static WindowInfo W(int handle, string title) => new(new IntPtr(handle), title, null, IntPtr.Zero, false, null);

    private static List<SwitcherItem> Items(params string[] titles) =>
        titles.Select((t, i) => new SwitcherItem(new[] { W(i + 1, t) })).ToList();

    private static List<string> Titles(IEnumerable<SwitcherItem> items) => items.Select(i => i.Shown.Title).ToList();

    [Fact]
    public void Empty_query_keeps_everything_in_order()
    {
        var items = Items("b", "a", "c");
        Assert.Equal(new[] { "b", "a", "c" }, Titles(WindowSearch.Filter(items, "  ", SearchMode.Fuzzy, w => w.Title)));
    }

    [Fact]
    public void Contains_mode_needs_the_text_as_typed_ignoring_case()
    {
        var items = Items("Inbox - Outlook", "GitHub - Chrome", "Notepad");
        Assert.Equal(new[] { "GitHub - Chrome" }, Titles(WindowSearch.Filter(items, "CHROME", SearchMode.Contains, w => w.Title)));
        Assert.Empty(WindowSearch.Filter(items, "chrm", SearchMode.Contains, w => w.Title));
    }

    [Fact]
    public void Fuzzy_mode_matches_letters_in_order()
    {
        var items = Items("Windows Terminal", "Notepad", "Task Manager");
        Assert.Equal(new[] { "Windows Terminal" }, Titles(WindowSearch.Filter(items, "wtm", SearchMode.Fuzzy, w => w.Title)));
        Assert.Empty(WindowSearch.Filter(items, "zzz", SearchMode.Fuzzy, w => w.Title));
    }

    [Fact]
    public void Every_word_must_match()
    {
        var items = Items("GitHub - Google Chrome", "Gmail - Google Chrome", "GitHub Desktop");
        Assert.Equal(new[] { "GitHub - Google Chrome" },
            Titles(WindowSearch.Filter(items, "chrome git", SearchMode.Contains, w => w.Title)));
    }

    [Fact]
    public void Better_matches_rank_first_and_ties_keep_recent_order()
    {
        // MRU order: "Docs" second, but "co" starts a word in "Code" and is only inside "Docs".
        var items = Items("Zoom", "Docs - Word", "Visual Studio Code", "Code review - Edge");
        var result = Titles(WindowSearch.Filter(items, "co", SearchMode.Fuzzy, w => w.Title));
        Assert.Equal("Code review - Edge", result[0]);     // word start at position 0
        Assert.Equal("Visual Studio Code", result[1]);     // word start, later
        Assert.Equal("Docs - Word", result[2]);            // inside a word
        Assert.DoesNotContain("Zoom", result);
    }

    [Fact]
    public void Contains_beats_fuzzy()
    {
        var terms = new[] { "note" };
        Assert.True(WindowSearch.Score("Notepad", terms, SearchMode.Fuzzy) >
                    WindowSearch.Score("No title entered", terms, SearchMode.Fuzzy));
    }

    [Fact]
    public void App_name_is_searchable_through_the_supplied_text()
    {
        var items = Items("Untitled", "README.md");
        var result = WindowSearch.Filter(items, "notepad", SearchMode.Fuzzy, w => w.Title + " " + (w.Title == "Untitled" ? "notepad" : "code"));
        Assert.Equal(new[] { "Untitled" }, Titles(result));
    }

    [Fact]
    public void A_stack_shows_its_best_matching_window()
    {
        var stack = new SwitcherItem(new[] { W(1, "Inbox - Chrome"), W(2, "GitHub - Chrome"), W(3, "News - Chrome") });
        var result = WindowSearch.Filter(new[] { stack }, "github", SearchMode.Fuzzy, w => w.Title);
        Assert.Equal("GitHub - Chrome", Assert.Single(result).Shown.Title);
    }

    [Theory]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", null, "WindowsTerminal")]
    [InlineData(null, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData("Chrome", @"C:\x\chrome.exe", "chrome")]
    [InlineData("Microsoft.Office.OUTLOOK.EXE.15", @"C:\Office\OUTLOOK.EXE", "OUTLOOK")]
    [InlineData(@"C:\my_apps\tool.exe", null, "tool")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Notepad++\notepad++.exe", null, "notepad++")]
    [InlineData(null, null, "")]
    public void App_names_are_friendly(string? aumid, string? path, string expected)
    {
        Assert.Equal(expected, WindowSearch.AppName(aumid, path));
    }
}
