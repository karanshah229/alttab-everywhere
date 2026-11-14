using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class WindowFilterTests
{
    private static readonly IntPtr Laptop = new(1);
    private static readonly IntPtr External = new(2);

    private static WindowInfo W(int handle, string title, string? app, IntPtr monitor) =>
        new(new IntPtr(handle), title, Icon: null, Monitor: monitor, IsMinimized: false, AppUserModelId: app);

    private static readonly WindowInfo[] Windows =
    {
        W(1, "Inbox - Outlook", "Microsoft.OutlookForWindows!App", Laptop),
        W(2, "notes.txt - Notepad", null, External),
        W(3, "Superwhisper", null, External),
        W(4, "Private browsing - Firefox", "Firefox", Laptop),
    };

    private static readonly Dictionary<int, string> Paths = new()
    {
        [1] = @"C:\Program Files\WindowsApps\Outlook\olk.exe",
        [2] = @"C:\Windows\System32\notepad.exe",
        [3] = @"C:\Users\me\AppData\Local\Superwhisper\superwhisper.exe",
        [4] = @"C:\Program Files\Mozilla Firefox\firefox.exe",
    };

    private static string? PathOf(WindowInfo w) => Paths.GetValueOrDefault((int)w.Handle);

    private static int[] Remaining(Settings s) =>
        WindowFilter.ApplyExclusions(Windows, s, PathOf).Select(w => (int)w.Handle).ToArray();

    [Fact]
    public void No_exclusions_keeps_everything_and_never_looks_up_paths()
    {
        var result = WindowFilter.ApplyExclusions(Windows, new Settings(), _ => throw new InvalidOperationException("should not be called"));
        Assert.Equal(4, result.Count);
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("NOTEPAD")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    public void Apps_match_by_exe_name_with_or_without_extension_or_full_path(string entry) =>
        Assert.Equal(new[] { 1, 3, 4 }, Remaining(new Settings { ExcludedApps = { entry } }));

    [Fact]
    public void Apps_match_by_app_id() =>
        Assert.Equal(new[] { 2, 3, 4 }, Remaining(new Settings { ExcludedApps = { "microsoft.outlookforwindows!app" } }));

    [Fact]
    public void Titles_match_by_case_insensitive_substring() =>
        Assert.Equal(new[] { 1, 2, 3 }, Remaining(new Settings { ExcludedTitles = { "PRIVATE BROWSING" } }));

    [Fact]
    public void App_and_title_exclusions_combine() =>
        Assert.Equal(new[] { 1 }, Remaining(new Settings { ExcludedApps = { "superwhisper", "notepad" }, ExcludedTitles = { "firefox" } }));

    [Fact]
    public void Blank_entries_are_ignored_after_normalizing()
    {
        var settings = new Settings { ExcludedApps = { "", "  " }, ExcludedTitles = { "" } }.Normalized();
        Assert.Equal(4, WindowFilter.ApplyExclusions(Windows, settings, PathOf).Count);
    }

    [Fact]
    public void All_monitors_mode_lists_every_window_everywhere() =>
        Assert.Equal(4, WindowFilter.ForMonitor(Windows, ListWindowsFrom.AllMonitors, Laptop).Count);

    [Fact]
    public void This_monitor_mode_lists_only_that_monitors_windows_in_order()
    {
        Assert.Equal(new[] { 1, 4 }, WindowFilter.ForMonitor(Windows, ListWindowsFrom.ThisMonitor, Laptop).Select(w => (int)w.Handle));
        Assert.Equal(new[] { 2, 3 }, WindowFilter.ForMonitor(Windows, ListWindowsFrom.ThisMonitor, External).Select(w => (int)w.Handle));
    }
}
