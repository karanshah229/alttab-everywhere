using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// The switcher overlay for one monitor. One instance exists per monitor and is reused across
/// Alt+Tab sessions. Built in code (no XAML) to keep the prototype small.
///
/// Note: AllowsTransparency must stay false — DWM thumbnails don't render on layered windows.
/// </summary>
internal sealed class SwitcherWindow : Window
{
    private const double WindowPadding = TileLayout.WindowPadding;


    /// <summary>Height of the search bar including its gap below, in DIPs (only shown while searching).</summary>
    public const double SearchBarHeight = 36;

    // A thin, dark scrollbar; the default WPF one is light grey and wide.
    private const string ScrollBarStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="8"/>
          <Setter Property="MinWidth" Value="8"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Track x:Name="PART_Track" IsDirectionReversed="True">
                  <Track.Thumb>
                    <Thumb>
                      <Thumb.Template>
                        <ControlTemplate TargetType="Thumb">
                          <Border CornerRadius="4" Background="{THUMB}"/>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;


    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;
    private readonly List<Tile> _tiles = new();
    private readonly Border _searchBar;
    private readonly TextBlock _searchText;
    private readonly TextBlock _emptyText;
    private readonly TextBlock _searchIcon;

    /// <summary>The colours this window was last drawn with; tiles are built per session from it.</summary>
    private Theme _theme = Theme.Current;

    public MonitorInfo Monitor { get; }
    public IntPtr Handle { get; }

    private int _columns = 1;
    private bool _showThumbnails = true;

    /// <summary>Tiles per row in the current session (the same on every monitor).</summary>
    public int ColumnCount => _columns;

    /// <summary>The most columns this monitor can fit for <paramref name="count"/> tiles.</summary>
    public int MaxColumns(int count, Settings settings)
    {
        var (width, height) = AvailableSpace();
        return TileLayout.MaxColumns(count, width, height, settings);
    }

    /// <summary>Whole rows visible at once for this grid (Page Up / Page Down).</summary>
    public int RowsPerPage(int count, Settings settings, int columns)
    {
        var (width, height) = AvailableSpace();
        return TileLayout.RowsPerPage(count, width, height, settings, columns);
    }

    private (double Width, double Height) AvailableSpace() =>
        TileLayout.Available(Monitor.WorkArea.Width / Monitor.Scale, Monitor.WorkArea.Height / Monitor.Scale);

    /// <summary>
    /// Tile bounds in DIPs relative to the tile grid, in list order (used by --selftest).
    /// Relative to the grid, not the window, so scrolling doesn't change them.
    /// </summary>
    public IReadOnlyList<Rect> TileRects =>
        _tiles.Select(t => new Rect(t.Root.TranslatePoint(new Point(0, 0), _panel), t.Root.RenderSize)).ToList();

    /// <summary>Whether a tile is entirely inside the scrolled viewport (used by --selftest).</summary>
    public bool IsTileFullyVisible(int index)
    {
        if (index < 0 || index >= _tiles.Count) return false;
        var root = _tiles[index].Root;
        var topLeft = root.TranslatePoint(new Point(0, 0), _scroll);
        return topLeft.Y >= -0.5 && topLeft.Y + root.ActualHeight <= _scroll.ViewportHeight + 0.5;
    }

    /// <summary>Whether the switcher stays within the size cap (75% x 75%) of its monitor's work area.</summary>
    public bool WithinSizeCap()
    {
        Native.GetWindowRect(Handle, out var rect);
        var work = Monitor.WorkArea;
        return rect.Width <= work.Width * TileLayout.MaxWidthFraction + 1 &&
               rect.Height <= work.Height * TileLayout.MaxHeightFraction + 1;
    }

    /// <summary>Height of the visible part of the tile grid, in DIPs (used by --selftest).</summary>
    public double ViewportHeight => _scroll.ViewportHeight;

    /// <summary>Whether the search bar is visible (used by --selftest).</summary>
    public bool IsSearchBarShown => _searchBar.Visibility == Visibility.Visible && IsVisible;

    /// <summary>Whether the current session needed a scrollbar on this monitor.</summary>
    public bool IsScrolling => _scroll.ExtentHeight > _scroll.ViewportHeight + 0.5;

    public event Action<int>? TileClicked;
    /// <summary>The × on a tile, or a middle-click (when enabled in settings).</summary>
    public event Action<int>? TileCloseRequested;
    /// <summary>The mouse moved onto a tile (raised only with hoverSelects, and only for real mouse movement).</summary>
    public event Action<int>? TileHovered;
    /// <summary>A stack's "×3" badge was clicked: open the stack.</summary>
    public event Action<int>? TileBadgeClicked;

    private bool _middleClickCloses = true;
    private bool _showCloseButton = true;
    private bool _hoverSelects;
    private POINT _lastCursor;   // hover ignores layout changes under a mouse that hasn't moved

    public SwitcherWindow(MonitorInfo monitor)
    {
        Monitor = monitor;

        Title = "AltTab Everywhere";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = _theme.Background;
        UseLayoutRounding = false; // tile widths must add up exactly for the fixed column count

        _scroll = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            // Keep the grid's position exact: the thumbnails are positioned from it.
            CanContentScroll = false,
        };
        _scroll.Resources.Add(typeof(ScrollBar), ScrollBarStyle(_theme));
        // Live previews are drawn by DWM on top of the window, so they must be moved (and
        // clipped) by hand whenever the grid scrolls.
        _scroll.ScrollChanged += (_, _) => UpdateThumbnails();

        // Search bar: only visible once something has been typed.
        _searchText = new TextBlock
        {
            Foreground = _theme.Text,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var searchIcon = _searchIcon = new TextBlock
        {
            Text = "", // Search glyph
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = _theme.DimText,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var searchRow = new DockPanel();
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(_searchText);
        _searchBar = new Border
        {
            Height = SearchBarHeight - 6,
            Margin = new Thickness(5, 0, 5, 6),
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Background = _theme.SearchBackground,
            Visibility = Visibility.Collapsed,
            Child = searchRow,
        };
        _emptyText = new TextBlock
        {
            Text = "No windows match",
            Foreground = _theme.DimText,
            FontSize = 13,
            Margin = new Thickness(10, 16, 10, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_searchBar, Dock.Top);
        DockPanel.SetDock(_emptyText, Dock.Top);
        layout.Children.Add(_searchBar);
        layout.Children.Add(_emptyText);
        layout.Children.Add(_scroll);
        Content = new Border { Padding = new Thickness(WindowPadding), Child = layout };

        Handle = new WindowInteropHelper(this).EnsureHandle();

        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = _theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Park the (still hidden) window on its monitor so WPF picks up that monitor's DPI.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, monitor.WorkArea.Left, monitor.WorkArea.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
    }

    /// <param name="columns">Shared across monitors so tiles line up the same way on every screen.</param>
    /// <param name="query">The search text; empty hides the search bar.</param>
    public void ShowSession(IReadOnlyList<SwitcherItem> items, int selectedIndex, Settings settings, int columns, string query = "")
    {
        ClearThumbnails();
        _panel.Children.Clear();
        _tiles.Clear();
        ApplyTheme(Theme.Current);
        _showThumbnails = settings.ShowThumbnails;
        _middleClickCloses = settings.MiddleClickCloses;
        _showCloseButton = settings.ShowCloseButton;
        _hoverSelects = settings.HoverSelects;
        Native.GetCursorPos(out _lastCursor);
        bool showNumbers = settings.NumberKeys != NumberKeys.Off && settings.ShowTileNumbers;

        bool searching = query.Length > 0;
        _searchBar.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        _searchText.Text = query;
        _emptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var (availWidth, availHeight) = AvailableSpace();
        if (searching) availHeight -= SearchBarHeight; // stay within the size cap with the search bar showing
        double scale = TileLayout.FitScale(items.Count, availWidth, availHeight, settings, columns);
        double tileWidth = settings.TileWidth * scale;
        double thumbHeight = settings.ThumbnailHeight * scale;
        // Exactly `columns` tiles per row. Layout rounding is off, so tile widths add up exactly;
        // the extra pixel only absorbs floating-point error.
        _panel.Width = columns * TileLayout.OuterWidth(settings, scale) + 1;
        _searchBar.Width = _panel.Width - 10; // a long query is trimmed, never widens the switcher
        _columns = columns;
        // Cap the visible height; anything taller scrolls inside the switcher.
        _scroll.MaxHeight = availHeight;
        _scroll.ScrollToTop();

        for (int i = 0; i < items.Count; i++)
        {
            int number = showNumbers ? SwitcherItem.NumberOf(items, i) : 0;
            var tile = CreateTile(i, tileWidth, thumbHeight, number > 0 ? number.ToString() : null, items[i].IsBack);
            Bind(tile, items[i]);
            _tiles.Add(tile);
            _panel.Children.Add(tile.Root);
        }
        SetSelected(selectedIndex);

        if (!IsVisible) Show();
        UpdateLayout();
        SetSelected(selectedIndex); // again, now that layout exists, so the selection scrolls into view
        UpdateLayout();
        Reposition();

        foreach (var tile in _tiles) RegisterThumbnail(tile);
        UpdateThumbnails();
    }

    public void SetSelected(int index)
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            bool selected = i == index;
            _tiles[i].Root.Background = selected ? _theme.SelectedBackground : Brushes.Transparent;
            _tiles[i].Root.BorderBrush = selected ? _theme.SelectedBorder : Brushes.Transparent;
        }
        // Scroll the selection into view; ScrollChanged then repositions the previews.
        if (index >= 0 && index < _tiles.Count) _tiles[index].Root.BringIntoView();
    }

    /// <summary>
    /// Cloaked = fully laid out and rendered, but not shown by DWM. The switcher is built cloaked
    /// as soon as Alt+Tab is pressed and uncloaked when the show delay ends, so the delay and the
    /// build overlap instead of adding up.
    /// </summary>
    public void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_CLOAK, ref value, sizeof(int));
    }

    public void EndSession()
    {
        ClearThumbnails();
        Hide();
        // Drop the tiles so their visuals, titles and icon references can be collected while idle.
        _panel.Children.Clear();
        _tiles.Clear();
    }

    /// <summary>The window a tile currently shows (used by --selftest).</summary>
    public IntPtr ShownHandle(int index) =>
        index >= 0 && index < _tiles.Count && _tiles[index].Window is { } w ? w.Handle : IntPtr.Zero;

    public string DescribeForLog()
    {
        Native.GetWindowRect(Handle, out var rect);
        var dpi = VisualTreeHelper.GetDpi(this);
        return $"monitor={Monitor.DeviceName} primary={Monitor.IsPrimary} work={Monitor.WorkArea} " +
               $"monitorScale={Monitor.Scale:0.##} wpfScale={dpi.DpiScaleX:0.##} overlay={rect} " +
               $"insideMonitor={Monitor.Bounds.Contains(rect)} visible={IsVisible} " +
               $"tiles={_tiles.Count} thumbnails={_tiles.Count(t => t.Thumbnail != IntPtr.Zero)}";
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbnails();
        base.OnClosed(e);
    }

    /// <summary>Builds an empty tile; <see cref="Bind"/> fills in the window it shows.</summary>
    /// <param name="number">"1".."9" for the number keys, or null.</param>
    /// <param name="isBack">An open stack's Back tile: an arrow instead of a preview, and no close button.</param>
    private Tile CreateTile(int index, double tileWidth, double thumbHeight, string? number, bool isBack)
    {
        var numberText = new TextBlock
        {
            Text = number ?? "",
            Foreground = _theme.DimText,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = number is null ? Visibility.Collapsed : Visibility.Visible,
        };

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var title = new TextBlock
        {
            Foreground = _theme.Text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // "×3" on a stack (click it to open the stack). Hidden for single windows.
        var badgeText = new TextBlock
        {
            Foreground = _theme.Text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Background = _theme.BadgeBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeText,
            ToolTip = "Show its windows",
        };
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // don't also switch to the window
            TileBadgeClicked?.Invoke(index);
        };

        // × to close the window; shown only while the mouse is over the tile.
        var close = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            ToolTip = "Close window",
            Child = new TextBlock
            {
                Text = "✕",
                Foreground = _theme.Text,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var closeGlyph = (TextBlock)close.Child;
        close.MouseEnter += (_, _) => { close.Background = _theme.CloseHoverBackground; closeGlyph.Foreground = _theme.CloseHoverText; };
        close.MouseLeave += (_, _) => { close.Background = Brushes.Transparent; closeGlyph.Foreground = _theme.Text; };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // don't also switch to the window
            TileCloseRequested?.Invoke(index);
        };

        var header = new DockPanel { Height = 24, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(numberText, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(numberText);
        header.Children.Add(icon);
        header.Children.Add(close);
        header.Children.Add(badge);
        header.Children.Add(title);

        // Shown when there's no live thumbnail (minimized windows, thumbnails off).
        double placeholderSize = Math.Min(48, thumbHeight * 0.5);
        var placeholder = new Image
        {
            Width = placeholderSize,
            Height = placeholderSize,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(placeholder, BitmapScalingMode.HighQuality);

        var thumbHost = new Border
        {
            Width = tileWidth,
            Height = thumbHeight,
            Background = _theme.ThumbBackground,
            CornerRadius = new CornerRadius(4),
            Child = isBack
                ? new TextBlock
                {
                    Text = "\uE72B", // back arrow
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = Math.Min(40, thumbHeight * 0.4),
                    Foreground = _theme.DimText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : placeholder,
        };

        // Fixed width: without it a long title widens its tile and the columns stop lining up.
        var stack = new StackPanel { Width = tileWidth };
        stack.Children.Add(header);
        stack.Children.Add(thumbHost);

        var root = new Border
        {
            Margin = new Thickness(5),
            Padding = new Thickness(6),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = stack,
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            TileClicked?.Invoke(index);
        };
        root.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || !_middleClickCloses) return;
            e.Handled = true;
            TileCloseRequested?.Invoke(index);
        };
        root.MouseMove += (_, _) =>
        {
            if (!_hoverSelects) return;
            // WPF also raises MouseMove when tiles scroll or appear under a still mouse; only a
            // real movement of the pointer should change the selection.
            Native.GetCursorPos(out var cursor);
            if (cursor.X == _lastCursor.X && cursor.Y == _lastCursor.Y) return;
            _lastCursor = cursor;
            TileHovered?.Invoke(index);
        };
        root.MouseEnter += (_, _) => { if (_showCloseButton && !isBack) close.Visibility = Visibility.Visible; };
        root.MouseLeave += (_, _) => close.Visibility = Visibility.Collapsed;

        return new Tile(root, thumbHost, icon, title, placeholder, badge, badgeText);
    }

    private static void Bind(Tile tile, SwitcherItem item)
    {
        if (item.IsBack)
        {
            tile.Window = null;
            tile.Title.Text = "Back";
            tile.Badge.Visibility = Visibility.Collapsed;
            return;
        }
        var window = item.Shown;
        tile.Window = window;
        tile.Icon.Source = window.Icon;
        tile.Placeholder.Source = window.Icon;
        tile.Title.Text = window.Title;
        tile.Badge.Visibility = item.IsStack ? Visibility.Visible : Visibility.Collapsed;
        tile.BadgeText.Text = $"×{item.Count}";
    }

    private void RegisterThumbnail(Tile tile)
    {
        // Minimized windows and windows on other desktops have nothing useful to show: keep the icon.
        if (!_showThumbnails || tile.Window is null || tile.Window.IsMinimized || tile.Window.OnOtherDesktop) return;
        if (Native.DwmRegisterThumbnail(Handle, tile.Window.Handle, out var thumb) == 0)
            tile.Thumbnail = thumb;
    }

    private static void UnregisterThumbnail(Tile tile)
    {
        if (tile.Thumbnail == IntPtr.Zero) return;
        Native.DwmUnregisterThumbnail(tile.Thumbnail);
        tile.Thumbnail = IntPtr.Zero;
    }

    private void UpdateThumbnails()
    {
        if (!IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(this);

        // The scroll viewport in window pixels: previews outside it are hidden, and previews
        // straddling its edge are cropped (DWM draws on top of the window and ignores WPF clipping).
        Point viewOrigin = _scroll.TransformToAncestor(this).Transform(new Point(0, 0));
        double viewTop = viewOrigin.Y * dpi.DpiScaleY;
        double viewBottom = (viewOrigin.Y + _scroll.ViewportHeight) * dpi.DpiScaleY;

        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            if (Native.DwmQueryThumbnailSourceSize(tile.Thumbnail, out var source) != 0 || source.cx <= 0 || source.cy <= 0)
                continue;

            // DWM wants physical pixels relative to this window's client area.
            Point origin = tile.ThumbHost.TransformToAncestor(this).Transform(new Point(0, 0));
            double boxLeft = origin.X * dpi.DpiScaleX;
            double boxTop = origin.Y * dpi.DpiScaleY;
            double boxWidth = tile.ThumbHost.ActualWidth * dpi.DpiScaleX;
            double boxHeight = tile.ThumbHost.ActualHeight * dpi.DpiScaleY;

            double fit = Math.Min(1.0, Math.Min(boxWidth / source.cx, boxHeight / source.cy));
            double width = source.cx * fit;
            double height = source.cy * fit;
            double left = boxLeft + (boxWidth - width) / 2;
            double top = boxTop + (boxHeight - height) / 2;

            // Crop to the viewport, mapping the visible slice back to source-window pixels.
            double visibleTop = Math.Max(top, viewTop);
            double visibleBottom = Math.Min(top + height, viewBottom);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_VISIBLE |
                          Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = 255,
                fSourceClientAreaOnly = 0,
            };
            if (visibleBottom - visibleTop < 1)
            {
                props.fVisible = 0; // scrolled out of view
            }
            else
            {
                props.fVisible = 1;
                props.rcDestination = new RECT((int)left, (int)visibleTop, (int)(left + width), (int)visibleBottom);
                props.rcSource = new RECT(0, (int)((visibleTop - top) / fit), source.cx, (int)((visibleBottom - top) / fit));
            }
            Native.DwmUpdateThumbnailProperties(tile.Thumbnail, ref props);
        }
    }

    private void ClearThumbnails()
    {
        foreach (var tile in _tiles) UnregisterThumbnail(tile);
    }

    /// <summary>Centers the overlay on its monitor's work area, in physical pixels.</summary>
    private void Reposition()
    {
        if (!IsVisible) return;
        Native.GetWindowRect(Handle, out var rect);
        var work = Monitor.WorkArea;
        int x = work.Left + (work.Width - rect.Width) / 2;
        int y = work.Top + (work.Height - rect.Height) / 2;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        UpdateThumbnails();
    }

    /// <summary>
    /// Recolours the parts of the window that outlive a session (tiles are rebuilt each session).
    /// Does nothing unless the theme changed since this window was last drawn.
    /// </summary>
    private void ApplyTheme(Theme theme)
    {
        if (ReferenceEquals(theme, _theme)) return;
        _theme = theme;
        Background = theme.Background;
        _searchBar.Background = theme.SearchBackground;
        _searchText.Foreground = theme.Text;
        _searchIcon.Foreground = theme.DimText;
        _emptyText.Foreground = theme.DimText;
        _scroll.Resources[typeof(ScrollBar)] = ScrollBarStyle(theme);
        int dark = theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // Parsed per window: each overlay has its own UI thread, and a Style can't be shared across threads.
    private static Style ScrollBarStyle(Theme theme) =>
        (Style)System.Windows.Markup.XamlReader.Parse(ScrollBarStyleXaml.Replace("{THUMB}", theme.ScrollThumb));

    private sealed class Tile(Border root, Border thumbHost, Image icon, TextBlock title, Image placeholder,
        Border badge, TextBlock badgeText)
    {
        public Border Root { get; } = root;
        public Border ThumbHost { get; } = thumbHost;
        public Image Icon { get; } = icon;
        public TextBlock Title { get; } = title;
        public Image Placeholder { get; } = placeholder;
        public Border Badge { get; } = badge;
        public TextBlock BadgeText { get; } = badgeText;
        /// <summary>The window this tile currently shows (changes when a stack is cycled).</summary>
        public WindowInfo? Window { get; set; }
        public IntPtr Thumbnail { get; set; }
    }
}
