using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AltTabEverywhere;

/// <summary>
/// A hidden top-level window on the main thread that receives system notifications, so the app can
/// react to them without polling or an extra thread:
/// resume from sleep, session unlock/reconnect, display changes, and Explorer restarting.
/// </summary>
internal sealed class SystemMessageWindow : IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const int WM_SYSCOLORCHANGE = 0x0015;   // a contrast theme was turned on or off
    private const int SPI_SETHIGHCONTRAST = 0x0043;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int WTS_CONSOLE_CONNECT = 0x1;
    private const int WTS_REMOTE_CONNECT = 0x3;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const int NOTIFY_FOR_THIS_SESSION = 0;

    private readonly HwndSource _source;
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    /// <summary>The keyboard hook may have been dropped (resume, unlock, reconnect).</summary>
    public event Action<string>? HookMayBeLost;
    public event Action? DisplayChanged;
    /// <summary>Windows' light/dark mode or accent colour changed.</summary>
    public event Action? ColorsChanged;
    /// <summary>Explorer (re)started: shell interfaces and the tray icon need refreshing.</summary>
    public event Action? ExplorerRestarted;

    public SystemMessageWindow()
    {
        // A real (not message-only) top-level window: only those receive broadcasts such as
        // WM_POWERBROADCAST and WM_DISPLAYCHANGE. It is never shown.
        var parameters = new HwndSourceParameters("AltTabEverywhere.SystemEvents")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        WTSRegisterSessionNotification(_source.Handle, NOTIFY_FOR_THIS_SESSION);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_POWERBROADCAST when (int)wParam is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC:
                HookMayBeLost?.Invoke("resume from sleep");
                break;
            case WM_WTSSESSION_CHANGE when (int)wParam is WTS_SESSION_UNLOCK or WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT:
                HookMayBeLost?.Invoke("session unlock/connect");
                break;
            case WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;
            case WM_SYSCOLORCHANGE:
            case WM_SETTINGCHANGE when (int)wParam == SPI_SETHIGHCONTRAST:
            case WM_DWMCOLORIZATIONCOLORCHANGED:
                ColorsChanged?.Invoke();
                break;
            case WM_SETTINGCHANGE when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                ColorsChanged?.Invoke(); // light/dark mode or accent changed
                break;
            default:
                if (msg == _taskbarCreated && _taskbarCreated != 0) ExplorerRestarted?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        WTSUnRegisterSessionNotification(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);
}
