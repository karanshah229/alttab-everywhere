using System;
using System.Globalization;

using Microsoft.Win32;

namespace AltTabEverywhere;

internal enum AppTheme
{
    /// <summary>Follow Windows' "app mode" (light or dark).</summary>
    System,
    Dark,
    Light,
}

/// <summary>Whether the switcher uses Windows' high-contrast colours.</summary>
internal enum HighContrastMode
{
    /// <summary>When Windows' high-contrast mode (a contrast theme) is on.</summary>
    System,
    /// <summary>Always, with Windows' current system colours.</summary>
    On,
    /// <summary>Never: the app's own theme even in a contrast theme.</summary>
    Off,
}

/// <summary>Windows' system colours, as a contrast theme sets them.</summary>
internal sealed record SystemColorSet(Argb Window, Argb WindowText, Argb Highlight, Argb HighlightText);

/// <summary>
/// The switcher's colours, independent of any UI framework (the settings window gets WPF brushes from
/// <see cref="WpfTheme"/>; the switcher draws with them directly). Built once
/// and cached (see <see cref="Current"/>); rebuilt only when settings change or Windows announces a
/// theme or accent change. Nothing is read from the registry per Alt+Tab.
/// </summary>
internal sealed class Theme
{
    public bool IsDark { get; }
    public Argb AccentColor { get; }
    public Argb Background { get; }
    public Argb Text { get; }
    public Argb DimText { get; }
    public Argb SelectedBackground { get; }
    public Argb SelectedBorder { get; }
    public Argb ThumbBackground { get; }
    public Argb BadgeBackground { get; }
    public Argb SearchBackground { get; }
    public Argb CloseHoverBackground { get; }
    public Argb CloseHoverText { get; }
    /// <summary>The scrollbar thumb.</summary>
    public Argb ScrollThumb { get; }
    /// <summary>Built from Windows' contrast-theme colours: solid colours only, and a thicker selection border.</summary>
    public bool IsHighContrast { get; }
    /// <summary>Width of the selected tile's border.</summary>
    public double SelectionThickness { get; }

    private static readonly Argb DefaultAccent = Argb.FromRgb(0x00, 0x78, 0xD4); // Windows' default blue

    private static volatile Theme? _current;

    /// <summary>The theme for the current settings and Windows colours (cached).</summary>
    public static Theme Current => _current ?? Rebuild(new Settings());

    /// <summary>Rebuilds the cached theme from the settings and Windows' current colours.</summary>
    public static Theme Rebuild(Settings settings)
    {
        var system = WindowsColors.Read();
        bool useContrast = settings.HighContrast == HighContrastMode.On ||
                           (settings.HighContrast == HighContrastMode.System && WindowsColors.HighContrastOn());
        var theme = Create(settings, system.AppsUseLightTheme, system.Palette, useContrast ? WindowsColors.SystemColors() : null);
        _current = theme;
        return theme;
    }

    /// <param name="systemLight">Windows' app mode is light.</param>
    /// <param name="systemPalette">Windows' accent palette (Light3..Dark3), or null if unavailable.</param>
    /// <param name="contrast">Windows' system colours when the high-contrast theme applies; they then win over every colour setting.</param>
    public static Theme Create(Settings settings, bool systemLight, Argb[]? systemPalette, SystemColorSet? contrast = null)
    {
        if (contrast is not null) return new Theme(contrast);

        bool dark = settings.Theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !systemLight,
        };

        Argb background = TryParseColor(settings.BackgroundColor, out var bg) ? bg
            : dark ? Argb.FromRgb(0x20, 0x20, 0x20) : Argb.FromRgb(0xF3, 0xF3, 0xF3);
        // A custom background decides light or dark text, so a light colour on the dark theme still reads.
        if (TryParseColor(settings.BackgroundColor, out _)) dark = Luminance(background) < 0.5;

        Argb text = TryParseColor(settings.TextColor, out var fg) ? fg
            : dark ? Argb.FromRgb(0xF5, 0xF5, 0xF5) : Argb.FromRgb(0x1A, 0x1A, 0x1A);

        Argb accent = AccentFor(settings.AccentColor, dark, systemPalette);
        return new Theme(dark, background, text, accent);
    }

    private Theme(bool dark, Argb background, Argb text, Argb accent)
    {
        IsDark = dark;
        AccentColor = accent;
        Background = background;
        Text = text;
        DimText = text.WithAlpha(0x99);
        SelectedBackground = text.WithAlpha(dark ? (byte)0x30 : (byte)0x1C);
        SelectedBorder = accent;
        ThumbBackground = Argb.Black.WithAlpha(dark ? (byte)0x50 : (byte)0x12);
        BadgeBackground = text.WithAlpha(dark ? (byte)0x40 : (byte)0x1E);
        SearchBackground = text.WithAlpha(dark ? (byte)0x26 : (byte)0x14);
        CloseHoverBackground = Argb.FromRgb(0xC4, 0x2B, 0x1C);
        CloseHoverText = Argb.White;
        ScrollThumb = text.WithAlpha(0x66);

        SelectionThickness = 2;
    }

    /// <summary>
    /// A contrast theme: only the colours Windows gives, never blended or see-through, so text keeps
    /// the contrast the user chose. The selection is a thick border in the highlight colour.
    /// </summary>
    private Theme(SystemColorSet c)
    {
        IsHighContrast = true;
        IsDark = Luminance(c.Window) < 0.5;
        AccentColor = c.Highlight;
        Background = c.Window;
        Text = c.WindowText;
        DimText = Text;
        SelectedBackground = Background;
        SelectedBorder = c.Highlight;
        ThumbBackground = Background;
        BadgeBackground = Background;
        SearchBackground = Background;
        CloseHoverBackground = c.Highlight;
        CloseHoverText = c.HighlightText;
        ScrollThumb = c.WindowText;
        SelectionThickness = 3; // tiles give up a pixel of margin for it, so layout is unchanged
    }

    /// <summary>
    /// "system" (or empty) uses Windows' accent, in the shade Windows itself uses on that background:
    /// a lighter shade on dark, a darker one on light. "#RRGGBB" picks a colour.
    /// </summary>
    internal static Argb AccentFor(string? setting, bool dark, Argb[]? palette)
    {
        if (TryParseColor(setting, out var custom)) return custom;
        if (palette is { Length: >= 7 }) return dark ? palette[1] : palette[4]; // Light2 / Dark1
        return dark ? Argb.FromRgb(0x60, 0xCD, 0xFF) : DefaultAccent;
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" (the # is optional). Anything else is "not set".</summary>
    internal static bool TryParseColor(string? text, out Argb color) => Argb.TryParse(text, out color);

    internal static double Luminance(Argb c) => c.Luminance;

    /// <summary>Windows' app mode and accent palette, from the same registry keys Settings writes.</summary>
    private static class WindowsColors
    {
        public static (bool AppsUseLightTheme, Argb[]? Palette) Read()
        {
            bool light = false;
            Argb[]? palette = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = key?.GetValue("AppsUseLightTheme") is int value && value != 0;
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (key?.GetValue("AccentPalette") is byte[] bytes && bytes.Length >= 28)
                    {
                        palette = new Argb[bytes.Length / 4];
                        for (int i = 0; i < palette.Length; i++)
                            palette[i] = Argb.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"theme: could not read Windows colours: {ex.Message}");
            }
            return (light, palette);
        }

        /// <summary>Windows' high-contrast mode (a contrast theme) is on.</summary>
        public static bool HighContrastOn()
        {
            var info = new HIGHCONTRAST { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<HIGHCONTRAST>() };
            return SystemParametersInfo(SPI_GETHIGHCONTRAST, (uint)info.cbSize, ref info, 0) && (info.dwFlags & HCF_HIGHCONTRASTON) != 0;
        }

        /// <summary>The system colours a contrast theme sets (window, text, selection).</summary>
        public static SystemColorSet SystemColors() =>
            new(Sys(COLOR_WINDOW), Sys(COLOR_WINDOWTEXT), Sys(COLOR_HIGHLIGHT), Sys(COLOR_HIGHLIGHTTEXT));

        private static Argb Sys(int index)
        {
            uint c = GetSysColor(index); // 0x00BBGGRR
            return Argb.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        }

        private const uint SPI_GETHIGHCONTRAST = 0x0042;
        private const int HCF_HIGHCONTRASTON = 0x1;
        private const int COLOR_WINDOW = 5, COLOR_WINDOWTEXT = 8, COLOR_HIGHLIGHT = 13, COLOR_HIGHLIGHTTEXT = 14;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct HIGHCONTRAST
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr lpszDefaultScheme;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST info, uint winIni);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetSysColor(int index);
    }
}
