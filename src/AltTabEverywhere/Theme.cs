using System;
using System.Globalization;
using System.Windows.Media;
using Microsoft.Win32;

namespace AltTabEverywhere;

internal enum AppTheme
{
    /// <summary>Follow Windows' "app mode" (light or dark).</summary>
    System,
    Dark,
    Light,
}

/// <summary>
/// The switcher's colours: frozen brushes, so every overlay's UI thread can use them. Built once
/// and cached (see <see cref="Current"/>); rebuilt only when settings change or Windows announces a
/// theme or accent change. Nothing is read from the registry per Alt+Tab.
/// </summary>
internal sealed class Theme
{
    public bool IsDark { get; }
    public Color AccentColor { get; }
    public Brush Background { get; }
    public Brush Text { get; }
    public Brush DimText { get; }
    public Brush SelectedBackground { get; }
    public Brush SelectedBorder { get; }
    public Brush ThumbBackground { get; }
    public Brush BadgeBackground { get; }
    public Brush SearchBackground { get; }
    public Brush CloseHoverBackground { get; }
    public Brush CloseHoverText { get; }
    /// <summary>"#AARRGGBB" for the scrollbar thumb (it lives in a XAML style).</summary>
    public string ScrollThumb { get; }

    private static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4); // Windows' default blue

    private static volatile Theme? _current;

    /// <summary>The theme for the current settings and Windows colours (cached).</summary>
    public static Theme Current => _current ?? Rebuild(new Settings());

    /// <summary>Rebuilds the cached theme from the settings and Windows' current colours.</summary>
    public static Theme Rebuild(Settings settings)
    {
        var system = WindowsColors.Read();
        var theme = Create(settings, system.AppsUseLightTheme, system.Palette);
        _current = theme;
        return theme;
    }

    /// <param name="systemLight">Windows' app mode is light.</param>
    /// <param name="systemPalette">Windows' accent palette (Light3..Dark3), or null if unavailable.</param>
    public static Theme Create(Settings settings, bool systemLight, Color[]? systemPalette)
    {
        bool dark = settings.Theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !systemLight,
        };

        Color background = TryParseColor(settings.BackgroundColor, out var bg) ? bg
            : dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        // A custom background decides light or dark text, so a light colour on the dark theme still reads.
        if (TryParseColor(settings.BackgroundColor, out _)) dark = Luminance(background) < 0.5;

        Color text = TryParseColor(settings.TextColor, out var fg) ? fg
            : dark ? Color.FromRgb(0xF5, 0xF5, 0xF5) : Color.FromRgb(0x1A, 0x1A, 0x1A);

        Color accent = AccentFor(settings.AccentColor, dark, systemPalette);
        return new Theme(dark, background, text, accent);
    }

    private Theme(bool dark, Color background, Color text, Color accent)
    {
        IsDark = dark;
        AccentColor = accent;
        Background = Brush(background);
        Text = Brush(text);
        DimText = Brush(WithAlpha(text, 0x99));
        SelectedBackground = Brush(WithAlpha(text, dark ? (byte)0x30 : (byte)0x1C));
        SelectedBorder = Brush(accent);
        ThumbBackground = Brush(WithAlpha(Colors.Black, dark ? (byte)0x50 : (byte)0x12));
        BadgeBackground = Brush(WithAlpha(text, dark ? (byte)0x40 : (byte)0x1E));
        SearchBackground = Brush(WithAlpha(text, dark ? (byte)0x26 : (byte)0x14));
        CloseHoverBackground = Brush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        CloseHoverText = Brushes.White;
        var thumb = WithAlpha(text, 0x66);
        ScrollThumb = $"#{thumb.A:X2}{thumb.R:X2}{thumb.G:X2}{thumb.B:X2}";
    }

    /// <summary>
    /// "system" (or empty) uses Windows' accent, in the shade Windows itself uses on that background:
    /// a lighter shade on dark, a darker one on light. "#RRGGBB" picks a colour.
    /// </summary>
    internal static Color AccentFor(string? setting, bool dark, Color[]? palette)
    {
        if (TryParseColor(setting, out var custom)) return custom;
        if (palette is { Length: >= 7 }) return dark ? palette[1] : palette[4]; // Light2 / Dark1
        return dark ? Color.FromRgb(0x60, 0xCD, 0xFF) : DefaultAccent;
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" (the # is optional). Anything else is "not set".</summary>
    internal static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        if (hex.Length == 6) value |= 0xFF000000;
        color = Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    internal static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Brush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Windows' app mode and accent palette, from the same registry keys Settings writes.</summary>
    private static class WindowsColors
    {
        public static (bool AppsUseLightTheme, Color[]? Palette) Read()
        {
            bool light = false;
            Color[]? palette = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = key?.GetValue("AppsUseLightTheme") is int value && value != 0;
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (key?.GetValue("AccentPalette") is byte[] bytes && bytes.Length >= 28)
                    {
                        palette = new Color[bytes.Length / 4];
                        for (int i = 0; i < palette.Length; i++)
                            palette[i] = Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"theme: could not read Windows colours: {ex.Message}");
            }
            return (light, palette);
        }
    }
}
