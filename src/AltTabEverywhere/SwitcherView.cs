using System;
using System.Collections.Generic;

namespace AltTabEverywhere;

/// <summary>A rectangle in device-independent pixels (no UI framework needed).</summary>
internal readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>What screen readers see for a tile (self-test).</summary>
/// <param name="Name">What is read out for it.</param>
/// <param name="IsListItem">Exposed as an item of the tile list.</param>
/// <param name="Position">1-based place in the list.</param>
/// <param name="Focused">Has keyboard focus within its switcher (follows the selection).</param>
/// <param name="Border">The selection border's width in DIPs.</param>
internal readonly record struct TileAccessibility(string Name, bool IsListItem, int Position, int Count, bool Focused, double Border);

/// <summary>
/// One monitor's switcher window, whatever draws it. All members are called on the window's own
/// thread (see <see cref="OverlayHost"/>), except the pure layout math.
/// </summary>
internal interface ISwitcherView
{
    MonitorInfo Monitor { get; }
    IntPtr Handle { get; }
    bool IsVisible { get; }

    event Action<int>? TileClicked;
    /// <summary>The × on a tile, or a middle-click (when enabled in settings).</summary>
    event Action<int>? TileCloseRequested;
    /// <summary>The mouse moved onto a tile (raised only with hoverSelects, and only for real mouse movement).</summary>
    event Action<int>? TileHovered;
    /// <summary>A stack's "×3" badge was clicked: open the stack.</summary>
    event Action<int>? TileBadgeClicked;
    event Action<int>? TileMoveHereClicked;
    event Action<int, TileMenuCommand>? TileMenuChosen;
    /// <summary>The window lost activation (another window was activated).</summary>
    event Action? SwitcherDeactivated;

    /// <summary>The most columns this monitor can fit for <paramref name="count"/> tiles (pure math; any thread).</summary>
    int MaxColumns(int count, Settings settings);
    /// <summary>Whole rows visible at once for this grid (Page Up / Page Down; pure math, any thread).</summary>
    int RowsPerPage(int count, Settings settings, int columns);

    /// <param name="columns">Shared across monitors so tiles line up the same way on every screen.</param>
    /// <param name="query">The search text; empty hides the search bar.</param>
    void ShowSession(IReadOnlyList<SwitcherItem> items, int selectedIndex, Settings settings, int columns, string query = "");
    void SetSelected(int index);
    /// <summary>Cloaked = laid out and drawn, but not shown by DWM.</summary>
    void SetCloaked(bool cloaked);
    void EndSession();
    void Close();

    // ---- Diagnostics and self-test ----

    /// <summary>Brings layout up to date before a query (WPF lays out lazily).</summary>
    void UpdateLayout();
    /// <summary>Tile bounds relative to the tile grid, in list order (scrolling doesn't change them).</summary>
    IReadOnlyList<RectD> TileRects { get; }
    bool IsTileFullyVisible(int index);
    bool WithinSizeCap();
    double ViewportHeight { get; }
    bool IsSearchBarShown { get; }
    bool IsScrolling { get; }
    /// <summary>iconsOnly: the selected window's title under the grid.</summary>
    string CaptionText { get; }
    /// <summary>Tile <paramref name="index"/>'s menu entries ("text|key"), built but not shown.</summary>
    IReadOnlyList<string> TileMenuEntries(int index);
    TileAccessibility AutomationOf(int index);
    IReadOnlyList<string> StateBadgeLabels(int index);
    /// <summary>The size of a tile's live preview, or (0, 0) if it shows the icon.</summary>
    (int Width, int Height) PreviewSourceSize(int index);
    /// <summary>The window a tile currently shows (zero for Back and Show desktop).</summary>
    IntPtr ShownHandle(int index);
    string DescribeForLog();
}

/// <summary>The window actions a tile's menu holds (the same in both switchers).</summary>
internal static class TileMenuModel
{
    /// <param name="Gesture">The key shown beside it, or "".</param>
    /// <param name="Checked">A tick (always on top).</param>
    /// <param name="Enabled">False for a window running as administrator: Windows only lets this app switch to it.</param>
    internal readonly record struct Entry(string Text, TileMenuCommand Command, string Gesture, bool Checked, bool Enabled);

    /// <summary>The disabled entries' explanation.</summary>
    public const string BlockedReason = "Runs as administrator: Windows doesn't let this app change it";

    /// <summary>The entries for <paramref name="hwnd"/>; a separator goes before <see cref="TileMenuCommand.Close"/>.</summary>
    public static List<Entry> For(IntPtr hwnd, Settings s, out bool blocked)
    {
        bool minimized = Interop.Native.IsIconic(hwnd);
        static string Hint(string key) => SwitcherKey.TryParse(key, out _) ? key : "";
        bool isBlocked = blocked = WindowActions.IsBlocked(hwnd);
        var entries = new List<Entry>(7);
        void Add(string text, TileMenuCommand command, string gesture = "", bool isChecked = false) =>
            entries.Add(new Entry(text, command, gesture, isChecked, !isBlocked || command == TileMenuCommand.Switch));
        Add("Switch to", TileMenuCommand.Switch, "Enter");
        Add("Move to this monitor", TileMenuCommand.MoveHere, Hint(s.MoveHereKey));
        Add(minimized ? "Restore" : "Minimize", TileMenuCommand.Minimize, Hint(s.MinimizeKey));
        Add(!minimized && WindowActions.ShouldMaximize(hwnd) ? "Maximize" : "Restore and switch", TileMenuCommand.Maximize, Hint(s.MaximizeKey));
        Add("Always on top", TileMenuCommand.AlwaysOnTop, Hint(s.AlwaysOnTopKey), WindowActions.IsTopmost(hwnd));
        Add("Close window", TileMenuCommand.Close, s.DeleteKeyCloses ? "Delete" : "");
        Add("Quit app (close all its windows)", TileMenuCommand.QuitApp, Hint(s.QuitAppKey));
        return entries;
    }
}
