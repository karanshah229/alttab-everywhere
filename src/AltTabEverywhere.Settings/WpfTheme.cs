using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AltTabEverywhere;

/// <summary>
/// A <see cref="Theme"/> as WPF needs it: frozen brushes, so every overlay's UI thread can share
/// them. Built once per theme and cached alongside it.
/// </summary>
internal sealed class WpfTheme
{
    private static readonly ConditionalWeakTable<Theme, WpfTheme> Cache = new();

    public static WpfTheme For(Theme theme) => Cache.GetValue(theme, t => new WpfTheme(t));

    public Theme Source { get; }
    public bool IsDark => Source.IsDark;
    public bool IsHighContrast => Source.IsHighContrast;
    public double SelectionThickness => Source.SelectionThickness;
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

    private WpfTheme(Theme t)
    {
        Source = t;
        AccentColor = ToColor(t.AccentColor);
        Background = Brush(t.Background);
        Text = Brush(t.Text);
        DimText = Brush(t.DimText);
        SelectedBackground = Brush(t.SelectedBackground);
        SelectedBorder = Brush(t.SelectedBorder);
        ThumbBackground = Brush(t.ThumbBackground);
        BadgeBackground = Brush(t.BadgeBackground);
        SearchBackground = Brush(t.SearchBackground);
        CloseHoverBackground = Brush(t.CloseHoverBackground);
        CloseHoverText = Brush(t.CloseHoverText);
        ScrollThumb = t.ScrollThumb.ToString();
    }

    public static Color ToColor(Argb c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Brush Brush(Argb c)
    {
        var brush = new SolidColorBrush(ToColor(c));
        brush.Freeze();
        return brush;
    }
}
