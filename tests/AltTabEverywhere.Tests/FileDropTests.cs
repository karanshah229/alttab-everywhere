using System.Runtime.InteropServices;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class FileDropTests
{
    private const string Own = @"C:\Apps\AltTabEverywhere\AltTabEverywhere.exe";

    [Fact]
    public void A_desktop_app_gets_the_files_on_its_command_line()
    {
        var plan = FileOpener.PlanFor(@"C:\Program Files\Notepad++\notepad++.exe", "Notepad++", frameHosted: false, Own);
        Assert.Equal(FileOpener.How.Exe, plan.How);
        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe", plan.Target);
    }

    [Fact]
    public void A_Store_app_is_activated_by_its_app_id()
    {
        const string id = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        Assert.Equal(new FileOpener.Plan(FileOpener.How.PackagedApp, id),
            FileOpener.PlanFor(@"C:\Windows\System32\ApplicationFrameHost.exe", id, frameHosted: true, Own));
        Assert.Equal(FileOpener.How.PackagedApp,
            FileOpener.PlanFor(@"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11\Notepad\Notepad.exe",
                "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", frameHosted: false, Own).How);
    }

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", "Microsoft.Windows.Explorer", false)] // Explorer would only open the file's default app
    [InlineData(Own, null, false)]                                                  // this app's own windows
    [InlineData(null, null, false)]                                                 // exe unreadable, no app ID
    [InlineData(@"C:\Program Files\WindowsApps\Some.App_1\app.exe", null, false)]   // packaged, but no app ID to activate
    [InlineData(@"C:\Windows\System32\ApplicationFrameHost.exe", null, true)]       // the frame host itself
    public void Some_windows_cant_take_files(string? exe, string? appId, bool frameHosted) =>
        Assert.Equal(FileOpener.How.NotSupported, FileOpener.PlanFor(exe, appId, frameHosted, Own).How);

    [Theory]
    [InlineData(@"C:\Docs\report.txt")]
    [InlineData(@"C:\My Docs\a b.txt")]
    [InlineData(@"\\server\share\x y\file.md")]
    [InlineData(@"C:\odd ""quoted"" name.txt")]
    [InlineData(@"C:\ends with backslash\")]
    [InlineData(@"C:\back\\slashes\\""quote.txt")]
    public void Every_path_reaches_the_app_exactly(string path)
    {
        string line = "app.exe " + FileOpener.Arguments(new[] { path, @"C:\second file.txt" });
        var parsed = Parse(line);
        Assert.Equal(new[] { "app.exe", path, @"C:\second file.txt" }, parsed);
    }

    [Fact]
    public void An_unknown_Store_app_fails_cleanly_without_opening_anything()
    {
        string file = Path.Combine(Path.GetTempPath(), $"alttab-drop-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "test");
        try
        {
            // Goes through the real COM activation call; a made-up app ID is refused, so nothing opens.
            Assert.False(FileOpener.Open(new FileOpener.Plan(FileOpener.How.PackagedApp, "AltTabEverywhere.Test_0000000000000!NoSuchApp"), new[] { file }));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Nothing_happens_without_files_or_a_target()
    {
        Assert.False(FileOpener.Open(new FileOpener.Plan(FileOpener.How.Exe, @"C:\x.exe"), Array.Empty<string>()));
        Assert.False(FileOpener.Open(new FileOpener.Plan(FileOpener.How.NotSupported, null), new[] { @"C:\a.txt" }));
    }

    [Fact]
    public void Dropping_files_is_on_by_default()
    {
        Assert.True(new Settings().DropFilesOnTiles);
        Assert.False(SettingsStore.Deserialize("""{ "dropFilesOnTiles": false }""").DropFilesOnTiles);
    }

    /// <summary>Windows' own command-line parser, as the target app will see it.</summary>
    private static string[] Parse(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
