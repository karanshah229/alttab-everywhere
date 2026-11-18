using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AltTabEverywhere.Interop;
using static AltTabEverywhere.Interop.Win32;

namespace AltTabEverywhere;

/// <summary>
/// The switcher for one monitor, drawn with plain Win32: GDI+ for the rounded shapes and icons, GDI
/// for ClearType text, DWM for the live previews. Layout is in DIPs (<see cref="TileLayout"/>), drawn at
/// the monitor's scale.
///
/// Nothing is kept between sessions but the window itself: tiles, fonts and previews are released
/// when a session ends, and the frame is drawn into a buffer that exists only while painting.
/// </summary>
internal sealed unsafe class SwitcherWindow : ISwitcherView, SwitcherAutomation.IHost
{
    private const double Pad = TileLayout.WindowPadding;
    /// <summary>Height of the search bar including its gap below, in DIPs (only shown while searching).</summary>
    private const double SearchBarHeight = 36;
    private const double ScrollBarWidth = 8;
    private const double LineSpacing = 1.33; // Segoe UI's line height per em, as WPF lays text out
    private const string TextFace = "Segoe UI";
    private const string BackGlyph = "";    // back arrow
    private const string DesktopGlyph = ""; // monitor
    private const string SearchGlyph = "";
    private const string CloseGlyph = "";

    public MonitorInfo Monitor { get; }
    public IntPtr Handle { get; }
    public bool IsVisible => _visible;

    public event Action<int>? TileClicked;
    public event Action<int>? TileCloseRequested;
    public event Action<int>? TileHovered;
    public event Action<int>? TileBadgeClicked;
    public event Action<int>? TileMoveHereClicked;
    public event Action<int, TileMenuCommand>? TileMenuChosen;
    public event Action? SwitcherDeactivated;

    private GCHandle _self;
    private bool _visible;
    private Theme _theme = Theme.Current;
    private double _scale = 1;

    // ---- The session ----
    private readonly List<Tile> _tiles = new();
    private IReadOnlyList<SwitcherItem> _items = Array.Empty<SwitcherItem>();
    private Settings _settings = new();
    private int _columns = 1;
    private string _query = "";
    private int _selected = -1;
    private SwitcherLayout _layout;
    private bool _showThumbnails, _minimizedPreviews, _middleClickCloses, _ctrlClickMovesHere, _showCloseButton, _hoverSelects, _announce;
    private Settings? _menuSettings;
    private bool _menuOpen;

    // ---- Layout, in DIPs ----
    private double _contentWidth;   // inside the window padding
    private double _panelWidth;     // the tile grid
    private double _gridTop;        // the grid's top in the window
    private double _extent, _viewport, _scroll;
    private double _emptyHeight, _captionHeight, _searchHeight;
    private double _clientWidth, _clientHeight;

    // ---- Mouse ----
    private int _hover = -1;
    private Part _hoverPart;
    private bool _tracking;
    private bool _dragging;
    private double _dragStartY, _dragStartScroll;
    private POINT _lastCursor;

    private readonly Dictionary<(int Size, int Weight, bool Glyph), IntPtr> _fonts = new();
    private readonly SwitcherAutomation _automation;

    /// <summary>A tile's parts; Part.State + k is its k-th state badge.</summary>
    private enum Part { None, Body, Close, Badge, ScrollThumb, State }

    public SwitcherWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        _automation = new SwitcherAutomation(this);
        EnsureProcessSetup();
        fixed (char* cls = ClassName)
        fixed (char* title = "AltTab Everywhere")
        {
            // Created on its monitor, so it gets that monitor's DPI (per-monitor v2).
            Handle = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, cls, title, WS_POPUP,
                monitor.WorkArea.Left, monitor.WorkArea.Top, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        }
        if (Handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        _self = GCHandle.Alloc(this);
        SetWindowLongPtrW(Handle, GWLP_USERDATA, GCHandle.ToIntPtr(_self));

        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        ApplyDarkMode();
    }

    // ---- Layout math (any thread) ----

    public int MaxColumns(int count, Settings settings)
    {
        var (width, height) = AvailableSpace(settings);
        return TileLayout.MaxColumns(count, width, height, settings);
    }

    public int RowsPerPage(int count, Settings settings, int columns)
    {
        var (width, height) = AvailableSpace(settings);
        return TileLayout.RowsPerPage(count, width, height, settings, columns);
    }

    private (double Width, double Height) AvailableSpace(Settings settings) =>
        TileLayout.Available(Monitor.WorkArea.Width / Monitor.Scale, Monitor.WorkArea.Height / Monitor.Scale, settings);

    // ---- Session ----

    public void ShowSession(IReadOnlyList<SwitcherItem> items, int selectedIndex, Settings settings, int columns, string query = "")
    {
        bool wasEmpty = _visible && _items.Count == 0;
        ShowSessionCore(items, selectedIndex, settings, columns, query);
        _automation.SessionChanged(wasEmpty);
    }

    private void ShowSessionCore(IReadOnlyList<SwitcherItem> items, int selectedIndex, Settings settings, int columns, string query)
    {
        ClearThumbnails();
        ReleaseFonts();
        _items = items;
        _settings = settings;
        _columns = columns;
        _query = query;
        if (!ReferenceEquals(_theme, Theme.Current))
        {
            _theme = Theme.Current;
            ApplyDarkMode();
        }
        _scale = GetDpiForWindow(Handle) / 96.0;
        _layout = settings.Layout;
        _showThumbnails = settings.ShowThumbnails && settings.Layout == SwitcherLayout.Thumbnails;
        _minimizedPreviews = settings.MinimizedPreviews;
        _middleClickCloses = settings.MiddleClickCloses;
        _ctrlClickMovesHere = settings.CtrlClickMovesHere;
        _menuSettings = settings.TileMenu ? settings : null;
        _showCloseButton = settings.ShowCloseButton;
        _hoverSelects = settings.HoverSelects;
        _announce = settings.ScreenReaderAnnouncements;
        _hover = -1;
        _hoverPart = Part.None;
        Native.GetCursorPos(out _lastCursor);

        BuildLayout();

        // Centred on the work area; the window has no frame, so its size is the client size.
        int width = (int)Math.Ceiling(_clientWidth * _scale), height = (int)Math.Ceiling(_clientHeight * _scale);
        var work = Monitor.WorkArea;
        SetWindowPos(Handle, Native.HWND_TOPMOST, work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2,
            width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _visible = true;

        _scroll = 0;
        _selected = -1;
        SetSelected(selectedIndex);
        foreach (var tile in _tiles) RegisterThumbnail(tile);
        UpdateThumbnails();
        InvalidateRect(Handle, null, 0);
        UpdateWindow(Handle); // drawn now: the caller reveals it next
    }

    public void SetSelected(int index)
    {
        int old = _selected;
        _selected = index;
        if (index >= 0 && index < _tiles.Count && ScrollIntoView(_tiles[index].Box))
        {
            InvalidateRect(Handle, null, 0);
            UpdateThumbnails();
        }
        else if (old == index) return;
        else
        {
            InvalidateTile(old);
            InvalidateTile(index);
            if (_layout == SwitcherLayout.IconsOnly) InvalidateDips(new RectD(Pad, _clientHeight - Pad - _captionHeight, _contentWidth, _captionHeight));
        }
        if (old != index) _automation.SelectionChanged();
    }

    public void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_CLOAK, ref value, sizeof(int));
    }

    public void EndSession()
    {
        if (_menuOpen) EndMenu();
        ClearThumbnails();
        ShowWindow(Handle, SW_HIDE);
        _visible = false;
        DestroyTooltip();
        if (_dragging) ReleaseCapture();
        _dragging = false;
        // Nothing of the session is kept while idle: tiles (their icons and titles) and fonts go.
        _tiles.Clear();
        _items = Array.Empty<SwitcherItem>();
        ReleaseFonts();
    }

    public void Close()
    {
        EndSession();
        _automation.Release();
        DestroyWindow(Handle);
        if (_self.IsAllocated) _self.Free();
    }

    public void UpdateLayout() { } // laid out eagerly in ShowSession

    // ---- Self-test and diagnostics ----

    public IReadOnlyList<RectD> TileRects => _tiles.Select(t => t.Box).ToList();

    public bool IsTileFullyVisible(int index) =>
        index >= 0 && index < _tiles.Count &&
        _tiles[index].Box.Top - _scroll >= -0.5 && _tiles[index].Box.Bottom - _scroll <= _viewport + 0.5;

    public bool WithinSizeCap()
    {
        Native.GetWindowRect(Handle, out var rect);
        var work = Monitor.WorkArea;
        return rect.Width <= work.Width * TileLayout.MaxWidthFraction + 1 &&
               rect.Height <= work.Height * TileLayout.MaxHeightFraction + 1;
    }

    public double ViewportHeight => _viewport;
    public bool IsSearchBarShown => _visible && _query.Length > 0;
    public bool IsScrolling => _extent > _viewport + 0.5;
    public string CaptionText => _visible && _layout == SwitcherLayout.IconsOnly && _tiles.Count > 0 && _selected >= 0 && _selected < _tiles.Count
        ? _tiles[_selected].Title : "";

    public IReadOnlyList<string> TileMenuEntries(int index) =>
        _menuSettings is { } s && index >= 0 && index < _tiles.Count && _tiles[index].Window is { } w
            ? TileMenuModel.For(w.Handle, s, out _).Select(e => $"{e.Text}|{e.Gesture}").ToList()
            : Array.Empty<string>();

    /// <summary>Read through the screen-reader provider itself, so the self-test checks what a screen reader gets.</summary>
    public TileAccessibility AutomationOf(int index)
    {
        if (_automation.Tile(index) is not { } node) return default;
        var name = node.GetPropertyValue(Uia.NameProperty);
        string text = name.AsString() ?? "";
        name.Clear();
        return new TileAccessibility(text, node.GetPropertyValue(Uia.ControlTypeProperty).AsInt() == Uia.ListItemControlType,
            node.GetPropertyValue(Uia.PositionInSetProperty).AsInt(), node.GetPropertyValue(Uia.SizeOfSetProperty).AsInt(),
            node.GetPropertyValue(Uia.HasKeyboardFocusProperty).AsBool(), _theme.SelectionThickness);
    }

    // ---- What the screen-reader provider reads (SwitcherAutomation.IHost) ----

    int SwitcherAutomation.IHost.TileCount => _tiles.Count;
    string SwitcherAutomation.IHost.TileName(int index) => _tiles[index].Name;
    int SwitcherAutomation.IHost.Selected => _selected;
    bool SwitcherAutomation.IHost.Announce => _announce;
    bool SwitcherAutomation.IHost.EmptyShown => _visible && _emptyHeight > 0;
    RECT SwitcherAutomation.IHost.TileScreenRect(int index) => ToScreen(Px(ToClient(_tiles[index].Box)));
    bool SwitcherAutomation.IHost.TileOffscreen(int index) =>
        !_visible || _tiles[index].Box.Bottom - _scroll <= 0 || _tiles[index].Box.Top - _scroll >= _viewport;
    RECT SwitcherAutomation.IHost.ListScreenRect => ToScreen(ViewportPx);
    RECT SwitcherAutomation.IHost.EmptyScreenRect => ToScreen(Px(new RectD(Pad, Pad + _searchHeight + 16, _contentWidth, 13 * LineSpacing)));

    int SwitcherAutomation.IHost.TileAtScreen(int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        ScreenToClient(Handle, &p);
        var (index, _) = HitTest(p.X, p.Y);
        return index;
    }

    private RECT ToScreen(RECT client)
    {
        Native.GetWindowRect(Handle, out var window); // no frame: the client area starts at the window's corner
        return new RECT(client.Left + window.Left, client.Top + window.Top, client.Right + window.Left, client.Bottom + window.Top);
    }

    public IReadOnlyList<string> StateBadgeLabels(int index) =>
        index < 0 || index >= _tiles.Count ? Array.Empty<string>() : _tiles[index].States.Select(s => s.Badge.Label).ToList();

    public (int Width, int Height) PreviewSourceSize(int index)
    {
        if (index < 0 || index >= _tiles.Count || _tiles[index].Thumbnail == IntPtr.Zero) return (0, 0);
        return Native.DwmQueryThumbnailSourceSize(_tiles[index].Thumbnail, out var s) == 0 ? (s.cx, s.cy) : (0, 0);
    }

    public IntPtr ShownHandle(int index) =>
        index >= 0 && index < _tiles.Count && _tiles[index].Window is { } w ? w.Handle : IntPtr.Zero;

    public string DescribeForLog()
    {
        Native.GetWindowRect(Handle, out var rect);
        return $"monitor={Monitor.DeviceName} primary={Monitor.IsPrimary} work={Monitor.WorkArea} " +
               $"monitorScale={Monitor.Scale:0.##} windowScale={GetDpiForWindow(Handle) / 96.0:0.##} overlay={rect} " +
               $"insideMonitor={Monitor.Bounds.Contains(rect)} visible={_visible} " +
               $"tiles={_tiles.Count} thumbnails={_tiles.Count(t => t.Thumbnail != IntPtr.Zero)} engine=win32";
    }

    // ---- Layout ----

    private sealed class Tile
    {
        public RectD Box;        // the tile's border box, relative to the grid (what TileRects reports)
        public RectD Header;     // the title row (empty in iconsOnly)
        public RectD ThumbBox;   // the preview / icon box (empty in the list)
        public string Title = "";
        public string Name = "";
        public string? Number;
        public string? Glyph;    // Back and Show desktop
        public string? BadgeText;
        public WindowInfo? Window;
        public AppIcon? Icon;
        public IntPtr Thumbnail;
        public readonly List<(StateBadge Badge, double Width)> States = new();
        public double NumberWidth, BadgeTextWidth, ArrowWidth;
    }

    /// <summary>Where a tile's title-row parts go (relative to the grid); depends on whether the × shows.</summary>
    private readonly record struct HeaderParts(RectD Number, RectD Arrow, RectD Icon, RectD Title, RectD Close, RectD Badge, RectD States);

    private void BuildLayout()
    {
        _tiles.Clear();
        var s = _settings;
        bool searching = _query.Length > 0;
        var (availWidth, availHeight) = AvailableSpace(s);
        if (searching) availHeight -= SearchBarHeight;
        double fit = TileLayout.FitScale(_items.Count, availWidth, availHeight, s, _columns);
        var geometry = TileLayout.Geometry(s);
        double contentScale = geometry.Scales ? fit : 1;
        double contentW = geometry.ContentWidth * contentScale, contentH = geometry.ContentHeight * contentScale;
        double iconSize = s.IconSize * contentScale;
        bool list = _layout == SwitcherLayout.List, iconsOnly = _layout == SwitcherLayout.IconsOnly;
        double outerW = TileLayout.OuterWidth(s, fit), outerH = TileLayout.OuterHeight(s, fit);
        double extra = _theme.SelectionThickness - 2;
        double mx = 5 - extra, my = list ? Math.Max(0, 1 - extra) : 5 - extra;
        double px = list ? 8 : 6, py = list ? 3 : 6;
        double border = _theme.SelectionThickness;
        bool showNumbers = s.NumberKeys != NumberKeys.Off && s.ShowTileNumbers;
        _iconSize = iconSize;

        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                int col = i % Math.Max(1, _columns), row = i / Math.Max(1, _columns);
                var box = new RectD(col * outerW + mx, row * outerH + my, outerW - 2 * mx, outerH - 2 * my);
                double cx = box.X + border + px, cy = box.Y + border + py;
                var tile = new Tile { Box = box };
                if (iconsOnly) tile.ThumbBox = new RectD(cx, cy, contentW, contentH);
                else
                {
                    tile.Header = new RectD(cx, cy, contentW, TileLayout.TitleRowHeight);
                    if (!list) tile.ThumbBox = new RectD(cx, cy + TileLayout.TitleRowHeight + 6, contentW, contentH);
                }

                int number = showNumbers ? SwitcherItem.NumberOf(_items, i) : 0;
                tile.Number = number > 0 ? number.ToString() : null;
                if (tile.Number is not null) tile.NumberWidth = Measure(dc, tile.Number, 11, FW_SEMIBOLD, false);
                if (!item.HasWindow)
                {
                    tile.Glyph = item.IsBack ? BackGlyph : DesktopGlyph;
                    tile.Title = item.IsBack ? "Back" : SwitcherItem.DesktopTitle;
                    tile.Name = TileAutomation.NameOf(item, Array.Empty<StateBadge>());
                    if (list) tile.ArrowWidth = Measure(dc, tile.Glyph, 14, FW_NORMAL, true);
                }
                else
                {
                    var window = item.Shown;
                    tile.Window = window;
                    tile.Icon = window.Icon;
                    tile.Title = window.Title;
                    if (item.IsStack)
                    {
                        tile.BadgeText = $"×{item.Count}";
                        tile.BadgeTextWidth = Measure(dc, tile.BadgeText, 11, FW_SEMIBOLD, false);
                    }
                    var states = StateBadges.For(window, s);
                    tile.Name = TileAutomation.NameOf(item, states);
                    foreach (var state in states) tile.States.Add((state, Measure(dc, state.Glyph, 12, FW_NORMAL, true) + 4));
                }

                _tiles.Add(tile);
            }

            // The window around the grid: search bar, "no windows" text, the grid, the iconsOnly caption.
            int rows = (_items.Count + Math.Max(1, _columns) - 1) / Math.Max(1, _columns);
            _panelWidth = _columns * outerW + 1;
            _extent = rows * outerH;
            _viewport = Math.Min(_extent, availHeight);
            _searchHeight = searching ? SearchBarHeight : 0;
            double emptyWidth = 0;
            _emptyHeight = 0;
            if (_items.Count == 0)
            {
                emptyWidth = Measure(dc, "No windows match", 13, FW_NORMAL, false) + 20;
                _emptyHeight = 16 + 13 * LineSpacing + 20;
            }
            _captionHeight = iconsOnly && _items.Count > 0 ? TileLayout.CaptionHeight : 0;
            _contentWidth = Math.Max(_panelWidth + (IsScrolling ? ScrollBarWidth : 0), emptyWidth);
            _gridTop = Pad + _searchHeight + _emptyHeight;
            _clientWidth = 2 * Pad + _contentWidth;
            _clientHeight = 2 * Pad + _searchHeight + _emptyHeight + _viewport + _captionHeight;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    // Set per session: the large icon's size in the icons layouts.
    private double _iconSize;
    private double IconSize => _iconSize;

    private HeaderParts LayoutHeader(Tile tile, bool closeShown)
    {
        var h = tile.Header;
        double mid = h.Y + h.Height / 2;
        RectD Centred(double x, double width, double height) => new(x, mid - height / 2, width, height);
        double left = h.X, right = h.Right;
        RectD number = default, arrow = default, icon = default, close = default, badge = default, states = default;
        if (tile.Number is not null)
        {
            number = Centred(left, tile.NumberWidth, 11 * LineSpacing);
            left += tile.NumberWidth + 6;
        }
        if (tile.Glyph is not null && _layout == SwitcherLayout.List)
        {
            arrow = Centred(left, tile.ArrowWidth, 14 * LineSpacing);
            left += tile.ArrowWidth + 8;
        }
        bool smallIcon = tile.Glyph is null && _layout is SwitcherLayout.Thumbnails or SwitcherLayout.List;
        if (smallIcon)
        {
            icon = Centred(left, 16, 16);
            left += 16 + 6;
        }
        if (closeShown)
        {
            close = Centred(right - 20, 20, 20);
            right -= 24;
        }
        if (tile.BadgeText is not null)
        {
            double width = tile.BadgeTextWidth + 12;
            badge = Centred(right - width, width, 11 * LineSpacing + 2);
            right -= width + 6;
        }
        if (tile.States.Count > 0)
        {
            double width = tile.States.Sum(s => s.Width);
            states = Centred(right - width, width, 12 * LineSpacing);
            right -= width + 4;
        }
        var title = new RectD(left, h.Y, Math.Max(0, right - left), h.Height);
        return new HeaderParts(number, arrow, icon, title, close, badge, states);
    }

    private bool CloseShown(int index) =>
        index == _hover && _showCloseButton && _layout != SwitcherLayout.IconsOnly &&
        index >= 0 && index < _tiles.Count && _tiles[index].Glyph is null;

    /// <summary>Scrolls the least amount that shows <paramref name="box"/> whole; true if it scrolled.</summary>
    private bool ScrollIntoView(RectD box)
    {
        double scroll = _scroll;
        if (box.Bottom - scroll > _viewport) scroll = box.Bottom - _viewport;
        if (box.Top < scroll) scroll = box.Top;
        return SetScroll(scroll);
    }

    private bool SetScroll(double scroll)
    {
        scroll = Math.Clamp(scroll, 0, Math.Max(0, _extent - _viewport));
        if (Math.Abs(scroll - _scroll) < 0.01) return false;
        _scroll = scroll;
        return true;
    }

    // ---- Coordinates: DIPs relative to the grid <-> window pixels ----

    private RectD ToClient(RectD r) => new(r.X + Pad, r.Y + _gridTop - _scroll, r.Width, r.Height);

    private RECT Px(RectD r) => new((int)Math.Round(r.X * _scale), (int)Math.Round(r.Y * _scale),
        (int)Math.Round(r.Right * _scale), (int)Math.Round(r.Bottom * _scale));

    private RECT ViewportPx => Px(new RectD(Pad, _gridTop, _panelWidth, _viewport));

    private void InvalidateDips(RectD clientRect)
    {
        var r = Px(clientRect);
        r.Left -= 1; r.Top -= 1; r.Right += 1; r.Bottom += 1; // antialiased edges
        InvalidateRect(Handle, &r, 0);
    }

    private void InvalidateTile(int index)
    {
        if (index >= 0 && index < _tiles.Count) InvalidateDips(ToClient(_tiles[index].Box));
    }

    // ---- Painting ----

    private void Paint()
    {
        PAINTSTRUCT ps;
        IntPtr dc = BeginPaint(Handle, &ps);
        try
        {
            RECT client;
            GetClientRect(Handle, &client);
            if (client.Width <= 0 || client.Height <= 0) return;

            // A buffer for this paint only: freed straight after, so nothing is held between paints.
            var info = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = client.Width, biHeight = -client.Height, biPlanes = 1, biBitCount = 32 };
            void* bits;
            IntPtr bitmap = CreateDIBSection(dc, &info, 0, &bits, IntPtr.Zero, 0);
            IntPtr mem = CreateCompatibleDC(dc);
            IntPtr oldBitmap = SelectObject(mem, bitmap);
            try
            {
                var clip = ps.rcPaint;
                IntersectClipRect(mem, clip.Left, clip.Top, clip.Right, clip.Bottom);
                var background = Opaque(_theme.Background);
                IntPtr brush = CreateSolidBrush(background.ToColorRef());
                FillRect(mem, &clip, brush);
                DeleteObject(brush);

                // Shapes and images with GDI+ (antialiased), then all text with GDI (ClearType) on top.
                IntPtr g;
                GdipCreateFromHDC(mem, &g);
                GdipSetSmoothingMode(g, SmoothingModeAntiAlias);
                GdipSetInterpolationMode(g, InterpolationModeHighQualityBicubic);
                GdipSetPixelOffsetMode(g, PixelOffsetModeHalf);
                GdipSetClipRectI(g, clip.Left, clip.Top, clip.Width, clip.Height, 0);
                PaintShapes(g, clip);
                GdipDeleteGraphics(g);
                SetBkMode(mem, TRANSPARENT);
                PaintText(mem, clip);

                BitBlt(dc, clip.Left, clip.Top, clip.Width, clip.Height, mem, clip.Left, clip.Top, SRCCOPY);
            }
            finally
            {
                SelectObject(mem, oldBitmap);
                DeleteDC(mem);
                DeleteObject(bitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Error("win32 switcher paint", ex);
        }
        finally
        {
            EndPaint(Handle, &ps);
        }
    }

    private Argb Opaque(Argb c) => c with { A = 0xFF };

    /// <summary><paramref name="top"/> blended over an opaque <paramref name="under"/>: GDI text has no alpha.</summary>
    private static Argb Over(Argb top, Argb under)
    {
        int a = top.A;
        byte Mix(byte t, byte u) => (byte)((t * a + u * (255 - a) + 127) / 255);
        return Argb.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private Argb TileBackground(int index) =>
        index == _selected ? Over(_theme.SelectedBackground, Opaque(_theme.Background)) : Opaque(_theme.Background);

    private static bool Intersects(RECT a, RECT b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private RECT Intersect(RECT a, RECT b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    private void PaintShapes(IntPtr g, RECT clip)
    {
        if (_searchHeight > 0)
            RoundRect(g, new RectD(Pad + 5, Pad, _panelWidth - 10, SearchBarHeight - 6), 6, _theme.SearchBackground);

        var viewport = Intersect(ViewportPx, clip);
        if (viewport.Width > 0 && viewport.Height > 0)
        {
            GdipSetClipRectI(g, viewport.Left, viewport.Top, viewport.Width, viewport.Height, 0);
            for (int i = 0; i < _tiles.Count; i++)
            {
                var tile = _tiles[i];
                var box = ToClient(tile.Box);
                if (!Intersects(Px(box), viewport)) continue;
                double radius = _layout == SwitcherLayout.List ? 6 : 8;
                double border = _theme.SelectionThickness;
                if (i == _selected)
                {
                    RoundRect(g, box, radius, _theme.SelectedBackground);
                    var inset = new RectD(box.X + border / 2, box.Y + border / 2, box.Width - border, box.Height - border);
                    RoundRectOutline(g, inset, radius - border / 2, _theme.SelectedBorder, border);
                }
                bool largeIcon = _layout is SwitcherLayout.Icons or SwitcherLayout.IconsOnly;
                if (tile.ThumbBox.Height > 0)
                {
                    var thumb = ToClient(tile.ThumbBox);
                    if (!largeIcon) RoundRect(g, thumb, 4, _theme.ThumbBackground);
                    if (tile.Glyph is null && tile.Thumbnail == IntPtr.Zero && tile.Icon is { } icon)
                    {
                        double size = largeIcon ? IconSize : Math.Min(48, tile.ThumbBox.Height * 0.5);
                        var at = new RectD(thumb.X + (thumb.Width - size) / 2, thumb.Y + (thumb.Height - size) / 2, size, size);
                        DrawIcon(g, icon, at, largeIcon || _theme.IsHighContrast ? 1f : 0.7f); // never faded in a contrast theme
                    }
                }
                if (_layout != SwitcherLayout.IconsOnly)
                {
                    var parts = LayoutHeader(tile, CloseShown(i));
                    if (parts.Icon.Width > 0 && tile.Icon is { } small) DrawIcon(g, small, ToClient(parts.Icon), 1f);
                    if (parts.Badge.Width > 0) RoundRect(g, ToClient(parts.Badge), 8, _theme.BadgeBackground);
                    if (parts.Close.Width > 0 && _hoverPart == Part.Close) RoundRect(g, ToClient(parts.Close), 4, _theme.CloseHoverBackground);
                }
                else if (tile.BadgeText is not null)
                {
                    RoundRect(g, ToClient(IconsOnlyBadge(tile)), 8, _theme.BadgeBackground);
                }
            }
            GdipSetClipRectI(g, clip.Left, clip.Top, clip.Width, clip.Height, 0);
        }

        if (IsScrolling)
        {
            double track = _viewport;
            double thumbLength = Math.Max(16, track * _viewport / _extent);
            double top = _gridTop + (track - thumbLength) * (_scroll / Math.Max(1, _extent - _viewport));
            RoundRect(g, new RectD(Pad + _panelWidth, top, ScrollBarWidth, thumbLength), 4, _theme.ScrollThumb);
        }
    }

    private RectD IconsOnlyBadge(Tile tile)
    {
        double width = tile.BadgeTextWidth + 12, height = 11 * LineSpacing + 2;
        return new RectD(tile.ThumbBox.Right - width, tile.ThumbBox.Bottom - height, width, height);
    }

    private void PaintText(IntPtr dc, RECT clip)
    {
        var background = Opaque(_theme.Background);
        if (_searchHeight > 0)
        {
            var bar = new RectD(Pad + 5 + 10, Pad, _panelWidth - 10 - 20, SearchBarHeight - 6);
            var under = Over(_theme.SearchBackground, background);
            double glyph = Measure(dc, SearchGlyph, 14, FW_NORMAL, true);
            Text(dc, SearchGlyph, new RectD(bar.X, bar.Y, glyph, bar.Height), 14, FW_NORMAL, true, Over(_theme.DimText, under), DT_VCENTER);
            Text(dc, _query, new RectD(bar.X + glyph + 8, bar.Y, Math.Max(0, bar.Width - glyph - 8), bar.Height), 14, FW_NORMAL, false,
                Over(_theme.Text, under), DT_VCENTER | DT_END_ELLIPSIS);
        }
        if (_emptyHeight > 0)
            Text(dc, "No windows match", new RectD(Pad, Pad + _searchHeight + 16, _contentWidth, 13 * LineSpacing), 13, FW_NORMAL, false,
                Over(_theme.DimText, background), DT_CENTER);
        if (_captionHeight > 0 && _selected >= 0 && _selected < _tiles.Count)
        {
            double width = _panelWidth - 10;
            var caption = new RectD(Pad + (_contentWidth - width) / 2 + 5, _clientHeight - Pad - _captionHeight + 7, width - 10, _captionHeight - 7);
            Text(dc, _tiles[_selected].Title, caption, 13, FW_NORMAL, false, Over(_theme.Text, background), DT_CENTER | DT_END_ELLIPSIS);
        }

        var viewport = Intersect(ViewportPx, clip);
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        int saved = SaveDC(dc);
        IntersectClipRect(dc, viewport.Left, viewport.Top, viewport.Right, viewport.Bottom);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (!Intersects(Px(ToClient(tile.Box)), viewport)) continue;
            var under = TileBackground(i);
            var text = Over(_theme.Text, under);
            var dim = Over(_theme.DimText, under);
            var badgeText = Over(_theme.Text, Over(_theme.BadgeBackground, under));

            if (_layout == SwitcherLayout.IconsOnly)
            {
                var box = ToClient(tile.ThumbBox);
                if (tile.Glyph is not null)
                    Text(dc, tile.Glyph, box, IconSize * 0.7, FW_NORMAL, true, dim, DT_CENTER | DT_VCENTER);
                if (tile.Number is not null)
                    Text(dc, tile.Number, new RectD(box.X, box.Y, tile.NumberWidth, 11 * LineSpacing), 11, FW_SEMIBOLD, false, dim, 0);
                if (tile.BadgeText is not null)
                    Text(dc, tile.BadgeText, ToClient(IconsOnlyBadge(tile)), 11, FW_SEMIBOLD, false, badgeText, DT_CENTER | DT_VCENTER);
                double x = box.Right - tile.States.Sum(s => s.Width);
                foreach (var (badge, width) in tile.States)
                {
                    Text(dc, badge.Glyph, new RectD(x, box.Y, width, 12 * LineSpacing), 12, FW_NORMAL, true, dim, DT_CENTER | DT_VCENTER);
                    x += width;
                }
                continue;
            }

            var parts = LayoutHeader(tile, CloseShown(i));
            if (tile.Number is not null) Text(dc, tile.Number, ToClient(parts.Number), 11, FW_SEMIBOLD, false, dim, DT_VCENTER);
            if (parts.Arrow.Width > 0) Text(dc, tile.Glyph!, ToClient(parts.Arrow), 14, FW_NORMAL, true, dim, DT_VCENTER);
            Text(dc, tile.Title, ToClient(parts.Title), 12, FW_NORMAL, false, text, DT_VCENTER | DT_END_ELLIPSIS);
            if (parts.States.Width > 0)
            {
                double x = ToClient(parts.States).X;
                foreach (var (badge, width) in tile.States)
                {
                    Text(dc, badge.Glyph, new RectD(x, ToClient(parts.States).Y, width, parts.States.Height), 12, FW_NORMAL, true, dim, DT_CENTER | DT_VCENTER);
                    x += width;
                }
            }
            if (parts.Badge.Width > 0) Text(dc, tile.BadgeText!, ToClient(parts.Badge), 11, FW_SEMIBOLD, false, badgeText, DT_CENTER | DT_VCENTER);
            if (parts.Close.Width > 0)
            {
                var color = _hoverPart == Part.Close ? Over(_theme.CloseHoverText, _theme.CloseHoverBackground) : text;
                Text(dc, CloseGlyph, ToClient(parts.Close), 10, FW_NORMAL, true, color, DT_CENTER | DT_VCENTER);
            }
            if (tile.Glyph is not null && tile.ThumbBox.Height > 0)
                Text(dc, tile.Glyph, ToClient(tile.ThumbBox), Math.Min(40, tile.ThumbBox.Height * 0.4), FW_NORMAL, true, dim, DT_CENTER | DT_VCENTER);
        }
        RestoreDC(dc, saved);
    }

    private void Text(IntPtr dc, string text, RectD dips, double size, int weight, bool glyph, Argb color, uint format)
    {
        if (text.Length == 0 || dips.Width <= 0) return;
        var r = Px(dips);
        IntPtr old = SelectObject(dc, Font(size, weight, glyph));
        SetTextColor(dc, color.ToColorRef());
        fixed (char* p = text) DrawTextW(dc, p, text.Length, &r, format | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(dc, old);
    }

    /// <summary>Text width in DIPs.</summary>
    private double Measure(IntPtr dc, string text, double size, int weight, bool glyph)
    {
        IntPtr old = SelectObject(dc, Font(size, weight, glyph));
        SIZE extent;
        fixed (char* p = text) GetTextExtentPoint32W(dc, p, text.Length, &extent);
        SelectObject(dc, old);
        return extent.cx / _scale;
    }

    private IntPtr Font(double sizeDip, int weight, bool glyph)
    {
        int px = Math.Max(1, (int)Math.Round(sizeDip * _scale));
        if (_fonts.TryGetValue((px, weight, glyph), out var font)) return font;
        fixed (char* face = glyph ? GlyphFace : TextFace)
            font = CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, face);
        _fonts[(px, weight, glyph)] = font;
        return font;
    }

    private void ReleaseFonts()
    {
        foreach (var font in _fonts.Values) DeleteObject(font);
        _fonts.Clear();
    }

    private void RoundRect(IntPtr g, RectD dips, double radius, Argb color)
    {
        if (color.A == 0 || dips.Width <= 0 || dips.Height <= 0) return;
        IntPtr path = RoundedPath(dips, radius);
        IntPtr brush;
        GdipCreateSolidFill(color.ToArgb32(), &brush);
        GdipFillPath(g, brush, path);
        GdipDeleteBrush(brush);
        GdipDeletePath(path);
    }

    private void RoundRectOutline(IntPtr g, RectD dips, double radius, Argb color, double width)
    {
        if (color.A == 0 || dips.Width <= 0 || dips.Height <= 0) return;
        IntPtr path = RoundedPath(dips, radius);
        IntPtr pen;
        GdipCreatePen1(color.ToArgb32(), (float)(width * _scale), UnitPixel, &pen);
        GdipDrawPath(g, pen, path);
        GdipDeletePen(pen);
        GdipDeletePath(path);
    }

    private IntPtr RoundedPath(RectD dips, double radius)
    {
        float x = (float)(dips.X * _scale), y = (float)(dips.Y * _scale), w = (float)(dips.Width * _scale), h = (float)(dips.Height * _scale);
        float d = (float)Math.Min(2 * Math.Max(0.5, radius) * _scale, Math.Min(w, h));
        IntPtr path;
        GdipCreatePath(0, &path);
        GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(path);
        return path;
    }

    private void DrawIcon(IntPtr g, AppIcon icon, RectD dips, float opacity)
    {
        fixed (byte* pixels = icon.Pixels)
        {
            IntPtr bitmap;
            if (GdipCreateBitmapFromScan0(icon.Width, icon.Height, icon.Width * 4, PixelFormat32bppPARGB, pixels, &bitmap) != 0) return;
            IntPtr attributes = IntPtr.Zero;
            if (opacity < 1f)
            {
                GdipCreateImageAttributes(&attributes);
                float* matrix = stackalloc float[25];
                for (int i = 0; i < 25; i++) matrix[i] = 0;
                matrix[0] = matrix[6] = matrix[12] = matrix[24] = 1;
                matrix[18] = opacity;
                GdipSetImageAttributesColorMatrix(attributes, 0, 1, matrix, null, 0);
            }
            GdipDrawImageRectRect(g, bitmap, (float)(dips.X * _scale), (float)(dips.Y * _scale), (float)(dips.Width * _scale), (float)(dips.Height * _scale),
                0, 0, icon.Width, icon.Height, UnitPixel, attributes, IntPtr.Zero, IntPtr.Zero);
            if (attributes != IntPtr.Zero) GdipDisposeImageAttributes(attributes);
            GdipDisposeImage(bitmap);
        }
    }

    // ---- Live previews ----

    private void RegisterThumbnail(Tile tile)
    {
        // Nothing for other desktops; minimized windows only with minimizedPreviews,
        // and only if they ever drew (not just their minimized caption).
        if (!_showThumbnails || tile.Window is not { } window || window.OnOtherDesktop) return;
        bool minimized = window.IsMinimized || Native.IsIconic(window.Handle);
        if (minimized && !_minimizedPreviews) return;
        if (Native.DwmRegisterThumbnail(Handle, window.Handle, out var thumb) != 0) return;
        if (minimized && (Native.DwmQueryThumbnailSourceSize(thumb, out var source) != 0 || TileLayout.IsMinimizedCaption(source.cx, source.cy)))
        {
            Native.DwmUnregisterThumbnail(thumb);
            return;
        }
        tile.Thumbnail = thumb;
    }

    private void UpdateThumbnails()
    {
        if (!_visible) return;
        double viewTop = _gridTop * _scale, viewBottom = (_gridTop + _viewport) * _scale;
        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            if (Native.DwmQueryThumbnailSourceSize(tile.Thumbnail, out var source) != 0 || source.cx <= 0 || source.cy <= 0) continue;
            var box = ToClient(tile.ThumbBox);
            double boxLeft = box.X * _scale, boxTop = box.Y * _scale, boxWidth = box.Width * _scale, boxHeight = box.Height * _scale;
            double fit = Math.Min(1.0, Math.Min(boxWidth / source.cx, boxHeight / source.cy));
            double width = source.cx * fit, height = source.cy * fit;
            double left = boxLeft + (boxWidth - width) / 2, top = boxTop + (boxHeight - height) / 2;
            double visibleTop = Math.Max(top, viewTop), visibleBottom = Math.Min(top + height, viewBottom);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_VISIBLE |
                          Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = 255,
            };
            if (visibleBottom - visibleTop < 1) props.fVisible = 0;
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
        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            Native.DwmUnregisterThumbnail(tile.Thumbnail);
            tile.Thumbnail = IntPtr.Zero;
        }
    }

    // ---- Mouse ----

    /// <summary>
    /// What is under a client point. Also leaves the part's bounds (relative to the grid) in
    /// <see cref="_hitRect"/>, for its tooltip.
    /// </summary>
    private (int Index, Part Part) HitTest(int xPx, int yPx)
    {
        double x = xPx / _scale, y = yPx / _scale;
        _hitRect = default;
        if (IsScrolling)
        {
            double thumbLength = Math.Max(16, _viewport * _viewport / _extent);
            double top = _gridTop + (_viewport - thumbLength) * (_scroll / Math.Max(1, _extent - _viewport));
            if (new RectD(Pad + _panelWidth, top, ScrollBarWidth, thumbLength).Contains(x, y)) return (-1, Part.ScrollThumb);
        }
        if (y < _gridTop || y >= _gridTop + _viewport) return (-1, Part.None);
        double gx = x - Pad, gy = y - _gridTop + _scroll;
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (!tile.Box.Contains(gx, gy)) continue;
            RectD states, badge = default, close = default;
            if (_layout == SwitcherLayout.IconsOnly)
            {
                double width = tile.States.Sum(s => s.Width);
                states = new RectD(tile.ThumbBox.Right - width, tile.ThumbBox.Y, width, 12 * LineSpacing);
                if (tile.BadgeText is not null) badge = IconsOnlyBadge(tile);
            }
            else
            {
                var parts = LayoutHeader(tile, WouldShowClose(i)); // the mouse is over it, so its × shows
                (states, badge, close) = (parts.States, parts.Badge, parts.Close);
            }
            if (close.Width > 0 && close.Contains(gx, gy)) return (i, Hit(Part.Close, close));
            if (badge.Width > 0 && badge.Contains(gx, gy)) return (i, Hit(Part.Badge, badge));
            if (states.Width > 0 && states.Contains(gx, gy))
            {
                double left = states.X;
                for (int k = 0; k < tile.States.Count; left += tile.States[k].Width, k++)
                    if (gx < left + tile.States[k].Width) return (i, Hit(Part.State + k, new RectD(left, states.Y, tile.States[k].Width, states.Height)));
            }
            return (i, Part.Body);
        }
        return (-1, Part.None);
    }

    private RectD _hitRect;

    private Part Hit(Part part, RectD bounds)
    {
        _hitRect = bounds;
        return part;
    }

    /// <summary>The × appears as soon as the mouse is over the tile.</summary>
    private bool WouldShowClose(int index) =>
        _showCloseButton && _layout != SwitcherLayout.IconsOnly && _tiles[index].Glyph is null;

    private void OnMouseMove(int x, int y)
    {
        if (!_tracking)
        {
            var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = Handle };
            TrackMouseEvent(&tme);
            _tracking = true;
        }
        if (_dragging)
        {
            double thumbLength = Math.Max(16, _viewport * _viewport / _extent);
            double perDip = (_extent - _viewport) / Math.Max(1, _viewport - thumbLength);
            if (SetScroll(_dragStartScroll + (y / _scale - _dragStartY) * perDip)) OnScrolled();
            return;
        }
        var (index, part) = HitTest(x, y);
        SetHover(index, part);
        if (_hoverSelects && index >= 0)
        {
            Native.GetCursorPos(out var cursor);
            if (cursor.X == _lastCursor.X && cursor.Y == _lastCursor.Y) return; // the tiles moved, not the mouse
            _lastCursor = cursor;
            TileHovered?.Invoke(index);
        }
    }

    private void SetHover(int index, Part part)
    {
        if (index == _hover && part == _hoverPart) return;
        int old = _hover;
        _hover = index;
        _hoverPart = part;
        InvalidateTile(old);
        if (index != old) InvalidateTile(index);
        UpdateTooltip(index, part, _hitRect);
    }

    // ---- Tooltips: the stack badge, the × and the state badges explain themselves ----

    private IntPtr _tooltip; // created on the first hover over such a part; destroyed when the session ends

    private string? TooltipText(int index, Part part) => part switch
    {
        _ when index < 0 || index >= _tiles.Count => null,
        Part.Close => "Close window",
        Part.Badge => "Show its windows",
        >= Part.State when (int)(part - Part.State) < _tiles[index].States.Count => _tiles[index].States[part - Part.State].Badge.Label,
        _ => null,
    };

    /// <summary>One tool, moved onto whichever part the mouse is over; the tooltip control handles the delays.</summary>
    private void UpdateTooltip(int index, Part part, RectD bounds)
    {
        string? text = TooltipText(index, part);
        if (text is null && _tooltip == IntPtr.Zero) return;
        if (_tooltip == IntPtr.Zero && !CreateTooltip()) return;
        var tool = new TOOLINFOW { cbSize = (uint)sizeof(TOOLINFOW), uFlags = TTF_SUBCLASS, hwnd = Handle, uId = 1 };
        SendMessageW(_tooltip, TTM_POP, 0, 0);
        tool.rect = text is null ? default : Px(ToClient(bounds));
        SendMessageW(_tooltip, TTM_NEWTOOLRECTW, 0, (nint)(&tool));
        fixed (char* p = text ?? "")
        {
            tool.lpszText = p;
            SendMessageW(_tooltip, TTM_UPDATETIPTEXTW, 0, (nint)(&tool));
        }
    }

    private bool CreateTooltip()
    {
        EnsureCommonControls();
        fixed (char* cls = "tooltips_class32")
            _tooltip = CreateWindowExW(WS_EX_TOPMOST, cls, null, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                0, 0, 0, 0, Handle, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_tooltip == IntPtr.Zero) return false;
        var tool = new TOOLINFOW { cbSize = (uint)sizeof(TOOLINFOW), uFlags = TTF_SUBCLASS, hwnd = Handle, uId = 1 };
        fixed (char* empty = "")
        {
            tool.lpszText = empty;
            SendMessageW(_tooltip, TTM_ADDTOOLW, 0, (nint)(&tool));
        }
        return true;
    }

    private void DestroyTooltip()
    {
        if (_tooltip == IntPtr.Zero) return;
        DestroyWindow(_tooltip);
        _tooltip = IntPtr.Zero;
    }

    private static bool _commonControls;

    private static void EnsureCommonControls()
    {
        if (_commonControls) return;
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = ICC_BAR_CLASSES };
        InitCommonControlsEx(&icc);
        _commonControls = true;
    }

    private void OnScrolled()
    {
        InvalidateRect(Handle, null, 0);
        UpdateThumbnails();
    }

    private void OnLeftDown(int x, int y)
    {
        if (HitTest(x, y).Part != Part.ScrollThumb) return;
        _dragging = true;
        _dragStartY = y / _scale;
        _dragStartScroll = _scroll;
        SetCapture(Handle);
    }

    private void OnLeftUp(int x, int y)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseCapture();
            return;
        }
        var (index, part) = HitTest(x, y);
        if (index < 0) return;
        switch (part)
        {
            case Part.Close:
                TileCloseRequested?.Invoke(index);
                break;
            case Part.Badge:
                TileBadgeClicked?.Invoke(index);
                break;
            default:
                // Ctrl from the real keyboard state: this window may not have keyboard focus.
                if (_ctrlClickMovesHere && (Native.GetAsyncKeyState(0x11 /* VK_CONTROL */) & 0x8000) != 0) TileMoveHereClicked?.Invoke(index);
                else TileClicked?.Invoke(index);
                break;
        }
    }

    private void OnMiddleUp(int x, int y)
    {
        var (index, _) = HitTest(x, y);
        if (index >= 0 && _middleClickCloses) TileCloseRequested?.Invoke(index);
    }

    private void OnRightUp(int x, int y)
    {
        var (index, _) = HitTest(x, y);
        if (index < 0 || _menuSettings is not { } s || _tiles[index].Window is not { } window) return;
        var entries = TileMenuModel.For(window.Handle, s, out bool blocked);
        IntPtr menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Command == TileMenuCommand.Close) AppendMenuW(menu, MF_SEPARATOR, 0, null);
                string text = e.Gesture.Length > 0 ? $"{e.Text}\t{e.Gesture}" : e.Text;
                uint flags = MF_STRING | (e.Checked ? MF_CHECKED : 0) | (e.Enabled ? 0 : MF_GRAYED);
                fixed (char* p = text) AppendMenuW(menu, flags, (nuint)(i + 1), p);
            }
            if (blocked)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                fixed (char* p = TileMenuModel.BlockedReason) AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, p);
            }
            ThemeMenus(_theme.IsDark);
            Native.GetCursorPos(out var at);
            SetForegroundWindow(Handle); // so a click elsewhere closes the menu
            _menuOpen = true;
            int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, at.X, at.Y, Handle, IntPtr.Zero);
            _menuOpen = false;
            if (chosen > 0 && chosen <= entries.Count && _visible) TileMenuChosen?.Invoke(index, entries[chosen - 1].Command);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OnWheel(int delta)
    {
        if (!IsScrolling) return;
        uint lines = 3;
        SystemParametersInfoW(0x0068 /* SPI_GETWHEELSCROLLLINES */, 0, &lines, 0);
        if (lines == 0 || lines > 100) lines = 3; // "one screen at a time" and the like: a few rows is plenty here
        if (SetScroll(_scroll - delta / 120.0 * 16 * lines)) OnScrolled();
    }

    private IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return 1; // all drawn in WM_PAINT
            case WM_MOUSEMOVE:
                OnMouseMove(x, y);
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                _tracking = false;
                SetHover(-1, Part.None);
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                OnLeftDown(x, y);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                OnLeftUp(x, y);
                return IntPtr.Zero;
            case WM_MBUTTONUP:
                OnMiddleUp(x, y);
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                OnRightUp(x, y);
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                OnWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                _dragging = false;
                return IntPtr.Zero;
            case WM_SETCURSOR:
                if ((lParam.ToInt64() & 0xFFFF) == 1 /* HTCLIENT */)
                {
                    POINT p;
                    GetCursorPos(&p);
                    ScreenToClient(Handle, &p);
                    var (index, part) = HitTest(p.X, p.Y);
                    SetCursor(index >= 0 ? HandCursor : ArrowCursor);
                    return 1;
                }
                break;
            case WM_ACTIVATE:
                if ((wParam.ToInt64() & 0xFFFF) == WA_INACTIVE) SwitcherDeactivated?.Invoke();
                return IntPtr.Zero;
            case WM_CLOSE:
                return IntPtr.Zero; // Alt+F4 while it has focus: only the app ends a session
            case WM_GETOBJECT:
                if (unchecked((int)lParam.ToInt64()) == Uia.RootObjectId) return _automation.OnGetObject(wParam, lParam);
                break;
            case WM_DPICHANGED:
                if (_visible) ShowSession(_items, _selected, _settings, _columns, _query);
                return IntPtr.Zero;
        }
        return DefWindowProcW(Handle, msg, wParam, lParam);
    }

    private void ApplyDarkMode()
    {
        int dark = _theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // ---- Process-wide setup ----

    private const string ClassName = "AltTabEverywhere.Switcher";
    private static readonly object SetupLock = new();
    private static bool _setUp;
    private static string GlyphFace = "Segoe Fluent Icons";
    private static IntPtr HandCursor, ArrowCursor;

    /// <summary>Registers the window class and starts GDI+ (once per process), and picks the symbol font.</summary>
    private static void EnsureProcessSetup()
    {
        lock (SetupLock)
        {
            if (_setUp) return;
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            IntPtr token;
            int status = GdiplusStartup(&token, &input, IntPtr.Zero);
            if (status != 0) throw new InvalidOperationException($"GDI+ failed to start ({status})");

            HandCursor = LoadCursorW(IntPtr.Zero, IDC_HAND);
            ArrowCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW);
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &StaticWndProc,
                    hInstance = GetModuleHandleW(null),
                    hCursor = ArrowCursor,
                    lpszClassName = name,
                };
                if (RegisterClassExW(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
            }

            // Windows 11 has Segoe Fluent Icons; Windows 10 has the same symbols in Segoe MDL2 Assets.
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr font;
            fixed (char* face = GlyphFace) font = CreateFontW(-16, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, 0, 0, face);
            IntPtr old = SelectObject(dc, font);
            char* actual = stackalloc char[64];
            int length = GetTextFaceW(dc, 64, actual);
            if (length == 0 || !string.Equals(new string(actual), GlyphFace, StringComparison.OrdinalIgnoreCase)) GlyphFace = "Segoe MDL2 Assets";
            SelectObject(dc, old);
            DeleteObject(font);
            DeleteDC(dc);
            _setUp = true;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr self = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (self == IntPtr.Zero) return DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            return ((SwitcherWindow)GCHandle.FromIntPtr(self).Target!).WndProc(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into Windows' message dispatch.
            Log.Error($"win32 switcher message 0x{msg:X}", ex);
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
