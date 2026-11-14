using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

    private static readonly Brush WindowBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly Brush SelectedBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SelectedBorder = Frozen(new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)));
    private static readonly Brush ThumbBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)));

    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly List<Tile> _tiles = new();

    public MonitorInfo Monitor { get; }
    public IntPtr Handle { get; }

    private int _columns = 1;

    /// <summary>Tiles per row in the current session (the same on every monitor).</summary>
    public int ColumnCount => _columns;

    /// <summary>The most columns this monitor can fit for <paramref name="count"/> tiles.</summary>
    public int MaxColumns(int count, Settings settings)
    {
        var (width, height) = AvailableSpace();
        return TileLayout.MaxColumns(count, width, height, settings);
    }

    private (double Width, double Height) AvailableSpace() =>
        TileLayout.Available(Monitor.WorkArea.Width / Monitor.Scale, Monitor.WorkArea.Height / Monitor.Scale);

    /// <summary>Tile bounds in DIPs relative to this window, in list order (used by --selftest).</summary>
    public IReadOnlyList<Rect> TileRects =>
        _tiles.Select(t => new Rect(t.Root.TranslatePoint(new Point(0, 0), this), t.Root.RenderSize)).ToList();

    public event Action<int>? TileClicked;

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
        Background = WindowBackground;
        UseLayoutRounding = false; // tile widths must add up exactly for the fixed column count
        Content = new Border { Padding = new Thickness(WindowPadding), Child = _panel };

        Handle = new WindowInteropHelper(this).EnsureHandle();

        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Park the (still hidden) window on its monitor so WPF picks up that monitor's DPI.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, monitor.WorkArea.Left, monitor.WorkArea.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
    }

    /// <param name="columns">Shared across monitors so tiles line up the same way on every screen.</param>
    public void ShowSession(IReadOnlyList<WindowInfo> windows, int selectedIndex, Settings settings, int columns)
    {
        ClearThumbnails();
        _panel.Children.Clear();
        _tiles.Clear();

        var (availWidth, availHeight) = AvailableSpace();
        double scale = TileLayout.FitScale(windows.Count, availWidth, availHeight, settings, columns);
        double tileWidth = settings.TileWidth * scale;
        double thumbHeight = settings.ThumbnailHeight * scale;
        // Exactly `columns` tiles per row. Layout rounding is off, so tile widths add up exactly;
        // the extra pixel only absorbs floating-point error.
        _panel.Width = columns * TileLayout.OuterWidth(settings, scale) + 1;
        _columns = columns;

        for (int i = 0; i < windows.Count; i++)
        {
            var tile = CreateTile(windows[i], i, tileWidth, thumbHeight);
            _tiles.Add(tile);
            _panel.Children.Add(tile.Root);
        }
        SetSelected(selectedIndex);

        if (!IsVisible) Show();
        UpdateLayout();
        Reposition();

        if (settings.ShowThumbnails) RegisterThumbnails();
        UpdateThumbnails();
    }

    public void SetSelected(int index)
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            bool selected = i == index;
            _tiles[i].Root.Background = selected ? SelectedBackground : Brushes.Transparent;
            _tiles[i].Root.BorderBrush = selected ? SelectedBorder : Brushes.Transparent;
        }
    }

    public void EndSession()
    {
        ClearThumbnails();
        Hide();
    }

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

    private Tile CreateTile(WindowInfo window, int index, double tileWidth, double thumbHeight)
    {
        var icon = new Image
        {
            Source = window.Icon,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var title = new TextBlock
        {
            Text = window.Title,
            Foreground = Brushes.WhiteSmoke,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { Height = 24, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(title);

        // Shown when there's no live thumbnail (minimized windows, thumbnails off).
        double placeholderSize = Math.Min(48, thumbHeight * 0.5);
        var placeholder = new Image
        {
            Source = window.Icon,
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
            Background = ThumbBackground,
            CornerRadius = new CornerRadius(4),
            Child = placeholder,
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

        return new Tile(root, thumbHost, window);
    }

    private void RegisterThumbnails()
    {
        foreach (var tile in _tiles)
        {
            if (tile.Window.IsMinimized) continue; // DWM has nothing useful to show for these
            if (Native.DwmRegisterThumbnail(Handle, tile.Window.Handle, out var thumb) == 0)
                tile.Thumbnail = thumb;
        }
    }

    private void UpdateThumbnails()
    {
        if (!IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(this);

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
            int width = (int)(source.cx * fit);
            int height = (int)(source.cy * fit);
            int left = (int)(boxLeft + (boxWidth - width) / 2);
            int top = (int)(boxTop + (boxHeight - height) / 2);

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE |
                          Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT(left, top, left + width, top + height),
                opacity = 255,
                fVisible = 1,
                fSourceClientAreaOnly = 0,
            };
            Native.DwmUpdateThumbnailProperties(tile.Thumbnail, ref props);
        }
    }

    private void ClearThumbnails()
    {
        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            Native.DwmUnregisterThumbnail(tile.Thumbnail);
            tile.Thumbnail = IntPtr.Zero;
        }
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

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class Tile(Border root, Border thumbHost, WindowInfo window)
    {
        public Border Root { get; } = root;
        public Border ThumbHost { get; } = thumbHost;
        public WindowInfo Window { get; } = window;
        public IntPtr Thumbnail { get; set; }
    }
}
