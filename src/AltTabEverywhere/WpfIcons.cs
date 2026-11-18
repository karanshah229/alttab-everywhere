using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AltTabEverywhere;

/// <summary>An <see cref="AppIcon"/> as a WPF image: converted once per icon, frozen so every overlay thread can share it.</summary>
internal static class WpfIcons
{
    private static readonly ConditionalWeakTable<AppIcon, System.Windows.Media.Imaging.BitmapSource> Cache = new();

    public static ImageSource? ToImage(AppIcon? icon) => icon is null ? null : Cache.GetValue(icon, static i =>
    {
        var image = System.Windows.Media.Imaging.BitmapSource.Create(i.Width, i.Height, 96, 96, PixelFormats.Pbgra32, null, i.Pixels, i.Width * 4);
        image.Freeze();
        return image;
    });
}
