using System;
using System.Runtime.InteropServices;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>Things you can do to the selected window from the switcher (a key or the tile's right-click menu).</summary>
internal enum WindowAction
{
    /// <summary>Switch to it and bring it to this monitor. Ends the session.</summary>
    MoveHere,
    /// <summary>Minimize it, or restore it (without switching) if it already is. The switcher stays open.</summary>
    Minimize,
    /// <summary>Switch to it, maximized (or restored if it already was). Ends the session.</summary>
    Maximize,
    /// <summary>Keep it above other windows, or stop. The switcher stays open.</summary>
    AlwaysOnTop,
    /// <summary>Ask every listed window of its app to close. The switcher stays open.</summary>
    QuitApp,
}

/// <summary>A tile's right-click menu: the window actions (same values) plus switching and closing.</summary>
internal enum TileMenuCommand
{
    MoveHere = WindowAction.MoveHere,
    Minimize = WindowAction.Minimize,
    Maximize = WindowAction.Maximize,
    AlwaysOnTop = WindowAction.AlwaysOnTop,
    QuitApp = WindowAction.QuitApp,
    Switch = 100,
    Close = 101,
}

internal static class WindowActions
{
    /// <summary>
    /// Windows refuses to let this app minimize, maximize, move, pin or close the window: it runs
    /// as administrator and this app doesn't. Switching to it still works.
    /// </summary>
    public static bool IsBlocked(IntPtr hwnd)
    {
        if (ProcessElevation.IsSelfElevated || ProcessElevation.HasUiAccess) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return ProcessElevation.IsElevated(pid);
    }

    /// <summary>Whether the action switches to the window and closes the switcher.</summary>
    public static bool EndsSession(WindowAction action) => action is WindowAction.MoveHere or WindowAction.Maximize;

    /// <summary>Minimizes the window, or restores it without activating it if it's minimized. Never waits on the app.</summary>
    /// <returns>Whether the window is minimized afterwards.</returns>
    public static bool ToggleMinimized(IntPtr hwnd)
    {
        bool minimized = Native.IsIconic(hwnd);
        ShowWindowAsync(hwnd, minimized ? SW_SHOWNOACTIVATE : SW_MINIMIZE);
        return !minimized;
    }

    /// <summary>Maximize, unless it's maximized and showing (then restore). Decide before switching, which un-minimizes it.</summary>
    public static bool ShouldMaximize(IntPtr hwnd) => !(IsZoomed(hwnd) && !Native.IsIconic(hwnd));

    public static void SetMaximized(IntPtr hwnd, bool maximize) => ShowWindowAsync(hwnd, maximize ? SW_SHOWMAXIMIZED : SW_RESTORE);

    /// <summary>Whether the window stays above others (WS_EX_TOPMOST).</summary>
    public static bool IsTopmost(IntPtr hwnd) =>
        (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Turns always-on-top on or off. Synchronous, so the caller can then put the switcher back on
    /// top (callers skip frozen windows). Elevated apps' windows refuse it unless this app is elevated too.
    /// </summary>
    /// <returns>Whether the window is on top afterwards.</returns>
    public static bool ToggleTopmost(IntPtr hwnd)
    {
        bool on = !IsTopmost(hwnd);
        Native.SetWindowPos(hwnd, on ? Native.HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOSIZE | SWP_NOMOVE | Native.SWP_NOACTIVATE);
        return IsTopmost(hwnd);
    }

    private const int SW_SHOWMAXIMIZED = 3, SW_SHOWNOACTIVATE = 4, SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const long WS_EX_TOPMOST = 0x8;
    private const uint SWP_NOMOVE = 0x0002;
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);
}
