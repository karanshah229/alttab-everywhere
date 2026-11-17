using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>
/// Peek: a coloured ring drawn over the edge of the selected window, wherever it is on screen.
/// A plain Win32 popup (no WPF, no message handler of our own: the class uses DefWindowProc and
/// paints with its background brush), shaped into a ring by a window region, click-through, and
/// kept in the topmost band just below the switcher. One per session; disposed when it ends.
/// </summary>
internal sealed class PeekOutline : IDisposable
{
    private const string ClassName = "AltTabEverywhere.Peek";
    private static bool _classRegistered;

    private IntPtr _hwnd;
    private IntPtr _brush;
    private Argb _color;

    /// <summary>The window the outline is on, or zero while hidden.</summary>
    public IntPtr Target { get; private set; }

    /// <summary>Where the outline is, in physical pixels (the target's frame), for the self-test.</summary>
    public RECT Bounds { get; private set; }

    /// <summary>
    /// Outlines <paramref name="target"/>, below the switcher windows in <paramref name="keepBelow"/>.
    /// Hides it (and returns false) if the window has nowhere visible to outline.
    /// </summary>
    public bool Show(IntPtr target, Argb color, double widthDip, IReadOnlyCollection<IntPtr> keepBelow)
    {
        if (target == IntPtr.Zero || Native.IsIconic(target) || !Native.IsWindowVisible(target) ||
            DwmGetWindowAttribute(target, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0 ||
            frame.Right - frame.Left < 8 || frame.Bottom - frame.Top < 8)
        {
            Hide();
            return false;
        }

        EnsureWindow();
        SetColor(color);

        int width = frame.Right - frame.Left, height = frame.Bottom - frame.Top;
        double scale = DpiScaleOf(target);
        var (thickness, radius) = Shape(widthDip, scale, maximized: IsZoomed(target));
        // A ring: the frame's rounded rectangle minus the same shape inset by the thickness.
        IntPtr ring = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        int innerRadius = Math.Max(0, radius - thickness);
        IntPtr inner = CreateRoundRectRgn(thickness, thickness, width - thickness + 1, height - thickness + 1, innerRadius * 2, innerRadius * 2);
        CombineRgn(ring, ring, inner, RGN_DIFF);
        DeleteObject(inner);
        SetWindowRgn(_hwnd, ring, true); // the system owns the region from here on

        Native.SetWindowPos(_hwnd, InsertAfter(keepBelow), frame.Left, frame.Top, width, height,
            Native.SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
        Target = target;
        Bounds = frame;
        return true;
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero && Target != IntPtr.Zero) Native.ShowWindow(_hwnd, SW_HIDE);
        Target = IntPtr.Zero;
    }

    /// <summary>Whether the outline window is on screen (self-test).</summary>
    public bool IsVisible => _hwnd != IntPtr.Zero && Native.IsWindowVisible(_hwnd);

    /// <summary>Self-test: whether the outline is below every one of <paramref name="windows"/> in the z-order.</summary>
    public bool IsBelowAll(IReadOnlyCollection<IntPtr> windows)
    {
        var remaining = new HashSet<IntPtr>(windows);
        for (IntPtr w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero && remaining.Count > 0; w = GetWindow(w, GW_HWNDNEXT))
        {
            if (w == _hwnd) return false;
            remaining.Remove(w);
        }
        return true;
    }

    /// <summary>Ring thickness and corner radius in physical pixels. Maximized windows have square corners.</summary>
    internal static (int Thickness, int Radius) Shape(double widthDip, double dpiScale, bool maximized)
    {
        int thickness = Math.Max(1, (int)Math.Round(widthDip * dpiScale));
        int radius = maximized ? 0 : (int)Math.Round(8 * dpiScale); // Windows 11's window corners
        return (thickness, radius);
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        IntPtr module = GetModuleHandle(null);
        if (!_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = module,
                lpszClassName = ClassName,
            };
            _classRegistered = RegisterClassEx(ref wc) != 0 || Marshal.GetLastWin32Error() == ERROR_CLASS_ALREADY_EXISTS;
        }
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"peek window: error {Marshal.GetLastWin32Error()}");
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA); // layered + transparent = clicks pass through
        int excluded = 1; // Windows' own peek (Win+, ) shouldn't keep it
        Native.DwmSetWindowAttribute(_hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref excluded, sizeof(int));
    }

    private void SetColor(Argb color)
    {
        if (_brush != IntPtr.Zero && color == _color) return;
        IntPtr old = _brush;
        _brush = CreateSolidBrush(color.ToColorRef());
        _color = color;
        SetClassLongPtr(_hwnd, GCLP_HBRBACKGROUND, _brush);
        if (old != IntPtr.Zero) DeleteObject(old);
        InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    /// <summary>The lowest of the switcher windows in the z-order, so the outline goes right under all of them.</summary>
    private static IntPtr InsertAfter(IReadOnlyCollection<IntPtr> keepBelow)
    {
        if (keepBelow.Count == 0) return Native.HWND_TOPMOST;
        var remaining = new HashSet<IntPtr>(keepBelow);
        IntPtr lowest = Native.HWND_TOPMOST;
        // Topmost windows come first in the z-order, so this only walks the (short) topmost band.
        for (IntPtr w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero && remaining.Count > 0; w = GetWindow(w, GW_HWNDNEXT))
        {
            if (remaining.Remove(w)) lowest = w;
        }
        return lowest;
    }

    private static double DpiScaleOf(IntPtr window)
    {
        IntPtr monitor = Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_brush != IntPtr.Zero)
        {
            DeleteObject(_brush); // safe now: no window of the class is left using it
            _brush = IntPtr.Zero;
        }
        Target = IntPtr.Zero;
    }

    // ---- Interop ----

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const int SW_HIDE = 0;
    private const int GCLP_HBRBACKGROUND = -10;
    private const int RGN_DIFF = 4;
    private const uint GW_HWNDNEXT = 2;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string name); // export names are ANSI: there is no W version

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
