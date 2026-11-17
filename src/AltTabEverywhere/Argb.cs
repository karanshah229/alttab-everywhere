using System.Globalization;

namespace AltTabEverywhere;

/// <summary>A colour with alpha, independent of any UI framework (WPF, GDI+, GDI all convert from it).</summary>
internal readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static readonly Argb Black = FromRgb(0, 0, 0);
    public static readonly Argb White = FromRgb(0xFF, 0xFF, 0xFF);

    public static Argb FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    public Argb WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>0xAARRGGBB, as GDI+ takes it.</summary>
    public uint ToArgb32() => (uint)A << 24 | (uint)R << 16 | (uint)G << 8 | B;

    /// <summary>0x00BBGGRR, as GDI's COLORREF (alpha dropped).</summary>
    public uint ToColorRef() => R | (uint)G << 8 | (uint)B << 16;

    /// <summary>Perceived brightness, 0 (black) to 1 (white).</summary>
    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;

    /// <summary>"#AARRGGBB".</summary>
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>"#RRGGBB" or "#AARRGGBB" (the # is optional). Anything else is "not set".</summary>
    public static bool TryParse(string? text, out Argb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        if (hex.Length == 6) value |= 0xFF000000;
        color = new Argb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}
