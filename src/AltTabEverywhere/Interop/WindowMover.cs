using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>
/// Moves a window onto another monitor, as Win+Shift+Arrow does: same size (shrunk only if it
/// doesn't fit), the same relative place on the screen, and the same state. A maximized window
/// is maximized on the new monitor; a minimized one is restored there when it's activated.
/// </summary>
internal static class WindowMover
{
    /// <returns>True if the window is on <paramref name="target"/> now (or already was).</returns>
    public static bool MoveTo(IntPtr hwnd, MonitorInfo target)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr current = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (current == target.Handle) return true;
        if (Native.IsHungAppWindow(hwnd)) return false; // moving would wait on a frozen app

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!Native.GetMonitorInfo(current, ref info) || !GetWindowPlacement(hwnd, ref placement)) return false;

        // The normal position is in "workspace" coordinates (relative to the primary monitor's work
        // area) unless the window is a tool window.
        long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        var (dx, dy) = (exStyle & Native.WS_EX_TOOLWINDOW) == 0 ? WorkspaceOffset() : (0, 0);
        var normal = Shift(placement.rcNormalPosition, dx, dy);

        var moved = MapRect(normal, info.rcWork, target.WorkArea);
        placement.rcNormalPosition = Shift(moved, -dx, -dy);

        bool maximized = IsZoomed(hwnd);
        bool minimized = Native.IsIconic(hwnd);
        // Maximized: restore onto the new monitor, then maximize there (Windows maximizes a window on
        // the monitor its normal position is on). Minimized: stays minimized, now belonging to the new monitor.
        placement.showCmd = minimized ? SW_SHOWMINNOACTIVE : SW_SHOWNOACTIVATE;
        if (!SetWindowPlacement(hwnd, ref placement)) return false;
        if (maximized) Native.ShowWindow(hwnd, SW_MAXIMIZE);
        return minimized || Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) == target.Handle;
    }

    /// <summary>
    /// Where a window goes on the new work area: same size unless that doesn't fit, centre at the
    /// same relative spot, and kept entirely inside.
    /// </summary>
    internal static RECT MapRect(RECT window, RECT from, RECT to)
    {
        int width = Math.Min(window.Width, to.Width);
        int height = Math.Min(window.Height, to.Height);
        double rx = ((window.Left + window.Right) / 2.0 - from.Left) / Math.Max(1, from.Width);
        double ry = ((window.Top + window.Bottom) / 2.0 - from.Top) / Math.Max(1, from.Height);
        int left = (int)Math.Round(to.Left + rx * to.Width - width / 2.0);
        int top = (int)Math.Round(to.Top + ry * to.Height - height / 2.0);
        left = Math.Clamp(left, to.Left, to.Right - width);
        top = Math.Clamp(top, to.Top, to.Bottom - height);
        return new RECT(left, top, left + width, top + height);
    }

    /// <summary>Workspace to screen: the primary monitor's work-area origin (non-zero with a taskbar on the left or top).</summary>
    private static (int, int) WorkspaceOffset()
    {
        var primary = Monitors.GetAll().FirstOrDefault(m => m.IsPrimary);
        return primary is null ? (0, 0) : (primary.WorkArea.Left - primary.Bounds.Left, primary.WorkArea.Top - primary.Bounds.Top);
    }

    private static RECT Shift(RECT r, int dx, int dy) => new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

    private const int SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7, SW_MAXIMIZE = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);
}
