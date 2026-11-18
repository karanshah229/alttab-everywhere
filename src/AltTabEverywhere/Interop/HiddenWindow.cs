using System;
using System.Runtime.InteropServices;
using static AltTabEverywhere.Interop.Win32;

namespace AltTabEverywhere.Interop;

/// <summary>
/// A hidden top-level window whose messages go to a handler: for system broadcasts (only top-level
/// windows get WM_POWERBROADCAST, WM_DISPLAYCHANGE, TaskbarCreated...) and the tray icon's callbacks.
/// Never shown.
/// </summary>
internal sealed unsafe class HiddenWindow : IDisposable
{
    /// <summary>Return true with <paramref name="result"/> to handle a message; false lets Windows handle it.</summary>
    public delegate bool Handler(uint msg, nint wParam, nint lParam, out nint result);

    private readonly Handler _handler;
    private GCHandle _self;

    public IntPtr Handle { get; private set; }

    /// <param name="style">Window styles; the default is a hidden popup. (The self-test makes visible test windows with it.)</param>
    /// <param name="exStyle">Extended styles; the default keeps it out of the taskbar and Alt+Tab.</param>
    /// <param name="owner">An owner window (an owned window stays out of the taskbar too).</param>
    public HiddenWindow(string name, Handler handler, uint style = WS_POPUP, int x = 0, int y = 0, int width = 0, int height = 0,
        uint exStyle = WS_EX_TOOLWINDOW, IntPtr owner = default)
    {
        _handler = handler;
        EnsureClass();
        fixed (char* cls = ClassName)
        fixed (char* title = name)
            Handle = CreateWindowExW(exStyle, cls, title, style, x, y, width, height, owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException($"{name}: CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        _self = GCHandle.Alloc(this);
        SetWindowLongPtrW(Handle, GWLP_USERDATA, GCHandle.ToIntPtr(_self));
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        SetWindowLongPtrW(Handle, GWLP_USERDATA, IntPtr.Zero);
        DestroyWindow(Handle);
        Handle = IntPtr.Zero;
        if (_self.IsAllocated) _self.Free();
    }

    private const string ClassName = "AltTabEverywhere.Hidden";
    private static readonly object ClassLock = new();
    private static bool _registered;

    private static void EnsureClass()
    {
        lock (ClassLock)
        {
            if (_registered) return;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &StaticWndProc, hInstance = GetModuleHandleW(null), lpszClassName = name };
                if (RegisterClassExW(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
            }
            _registered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr self = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (self != IntPtr.Zero)
        {
            try
            {
                if (((HiddenWindow)GCHandle.FromIntPtr(self).Target!)._handler(msg, wParam, lParam, out var result)) return result;
            }
            catch (Exception ex)
            {
                Log.Error($"window message 0x{msg:X}", ex);
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
