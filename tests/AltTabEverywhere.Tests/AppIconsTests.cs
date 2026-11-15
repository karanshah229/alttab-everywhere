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

    /// <summary>The Settings app ships with every Windows 10 and 11 install.</summary>
    [Fact]
    public void A_built_in_Store_app_gets_a_sharp_icon_with_transparency()
    {
        var icon = AppIcons.ForAppId("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")
            as System.Windows.Media.Imaging.BitmapSource;
        Assert.NotNull(icon);
        Assert.True(icon!.PixelWidth >= 48, $"icon is only {icon.PixelWidth} px");
        Assert.True(icon.IsFrozen); // shared with the overlays' threads
    }

    [Fact]
    public void An_unknown_app_id_gives_no_icon_instead_of_throwing() =>
        Assert.Null(AppIcons.ForAppId("NoSuchPackage_0000000000000!App"));
}
