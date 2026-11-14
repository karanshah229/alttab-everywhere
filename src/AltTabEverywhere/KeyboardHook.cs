using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// Global low-level keyboard hook that takes over Alt+Tab.
///
/// The hook callback must return fast (Windows silently removes hooks that time out), so it only
/// decides swallow / pass-through and posts the actual work to the dispatcher. The hook keeps its
/// own "session active" flag because posted work may not have run yet when the next key arrives.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private static readonly IntPtr Swallow = new(1);

    private readonly Native.LowLevelKeyboardProc _proc; // kept in a field so the GC can't collect it
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private bool _sessionActive;

    /// <summary>(isFirstTab, reverse) — reverse when Shift is held.</summary>
    public event Action<bool, bool>? Tab;
    /// <summary>-1 / +1: previous / next tile (Left/Right, or Tab once Alt is released in stay-open mode).</summary>
    public event Action<int>? Move;
    /// <summary>-1 / +1: one row up / down (Up/Down arrows).</summary>
    public event Action<int>? MoveRow;
    public event Action? AltReleased;
    public event Action? Commit;
    public event Action? Cancel;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Stay-open mode: releasing Alt doesn't end the hook's session, so Tab, arrows, Enter and Esc
    /// keep working. The controller still decides whether to commit (a quick tap commits).
    /// </summary>
    public bool StayOpen { get; set; }

    public KeyboardHook()
    {
        _proc = HookProc;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    /// <summary>Called by the controller when a session ends for any reason (click, Esc, Enter...).</summary>
    public void EndSession() => _sessionActive = false;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled)
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((key.flags & Native.LLKHF_INJECTED) == 0 && Handle(key)) return Swallow;
            }
        }
        catch (Exception ex)
        {
            Log.Error("keyboard hook", ex);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <returns>true to swallow the key.</returns>
    private bool Handle(KBDLLHOOKSTRUCT key)
    {
        int vk = (int)key.vkCode;
        bool isUp = (key.flags & Native.LLKHF_UP) != 0;
        bool altHeld = (key.flags & Native.LLKHF_ALTDOWN) != 0;

        // Alt+Tab / Alt+Shift+Tab. Leave Ctrl+Alt+Tab and Win combos to Windows.
        if (vk == VK_TAB && !isUp && altHeld && !IsDown(VK_CONTROL) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN))
        {
            bool first = !_sessionActive;
            bool reverse = IsDown(VK_SHIFT);
            _sessionActive = true;
            Post(() => Tab?.Invoke(first, reverse));
            return true;
        }

        if (!_sessionActive) return false;

        if ((vk is VK_LMENU or VK_RMENU or VK_MENU) && isUp)
        {
            // Releasing Alt normally picks the selected window. Let the Alt-up through so Windows
            // doesn't think Alt is stuck.
            if (!StayOpen) _sessionActive = false;
            Post(() => AltReleased?.Invoke());
            return false;
        }

        switch (vk)
        {
            case VK_TAB:
                // Alt+Tab was handled above; this is a bare Tab after Alt was released (stay-open mode).
                if (!isUp && !altHeld) { int d = IsDown(VK_SHIFT) ? -1 : +1; Post(() => Move?.Invoke(d)); }
                return true; // also swallows the Tab key-up
            case VK_ESCAPE:
                if (!isUp) { _sessionActive = false; Post(() => Cancel?.Invoke()); }
                return true;
            case VK_RETURN:
                if (!isUp) { _sessionActive = false; Post(() => Commit?.Invoke()); }
                return true;
            case VK_LEFT:
                if (!isUp) Post(() => Move?.Invoke(-1));
                return true;
            case VK_RIGHT:
                if (!isUp) Post(() => Move?.Invoke(+1));
                return true;
            case VK_UP:
                if (!isUp) Post(() => MoveRow?.Invoke(-1));
                return true;
            case VK_DOWN:
                if (!isUp) Post(() => MoveRow?.Invoke(+1));
                return true;
            default:
                return false;
        }
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
