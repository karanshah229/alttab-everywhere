using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class AppIconsTests
{
    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", true)]
    [InlineData("Chrome.UserData.Profile1", false)]
    [InlineData("Microsoft.Windows.Explorer", false)]
    [InlineData("!App", false)]
    [InlineData("Package!", false)]
    [InlineData(null, false)]
    public void Only_package_app_ids_count_as_Store_apps(string? id, bool packaged) =>
        Assert.Equal(packaged, AppIcons.IsPackagedAppId(id));

    /// <summary>The Settings app ships with every Windows 10 and 11 desktop (not always with the Server images CI runs on).</summary>
    [DesktopFact]
    public void A_built_in_Store_app_gets_a_sharp_icon_with_transparency()
    {
        var icon = AppIcons.ForAppId("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
        Assert.NotNull(icon);
        Assert.True(icon!.Width >= 48, $"icon is only {icon.Width} px");
        AssertPremultipliedWithTransparency(icon);
    }

    [Fact]
    public void An_unknown_app_id_gives_no_icon_instead_of_throwing() =>
        Assert.Null(AppIcons.ForAppId("NoSuchPackage_0000000000000!App"));

    [Fact]
    public void An_exe_gives_its_icon_as_premultiplied_pixels()
    {
        var icon = AppIcon.ForFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        Assert.NotNull(icon);
        Assert.Equal(icon!.Width * icon.Height * 4, icon.Pixels.Length);
        AssertPremultipliedWithTransparency(icon);
    }

    [Fact]
    public void A_missing_file_gives_no_icon() =>
        Assert.Null(AppIcon.ForFile(@"C:\no\such\file.exe"));

    private static void AssertPremultipliedWithTransparency(AppIcon icon)
    {
        bool transparent = false, opaque = false;
        for (int i = 0; i < icon.Pixels.Length; i += 4)
        {
            byte a = icon.Pixels[i + 3];
            Assert.True(icon.Pixels[i] <= a && icon.Pixels[i + 1] <= a && icon.Pixels[i + 2] <= a, $"pixel {i / 4} is not premultiplied");
            transparent |= a == 0;
            opaque |= a == 255;
        }
        Assert.True(transparent, "no transparent pixels");
        Assert.True(opaque, "no opaque pixels");
    }
}
