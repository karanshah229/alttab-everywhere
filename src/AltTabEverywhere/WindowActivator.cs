using System;
using System.Runtime.InteropServices;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

internal static class WindowActivator
{
    /// <summary>Brings a window to the foreground, restoring it if minimized.</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (Native.IsIconic(hwnd))
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            // Windows refuses that (access denied) for windows running as administrator when this app
            // isn't: they would get focus but stay minimized. SwitchToThisWindow, which Windows' own
            // switcher uses, is allowed and restores them.
            if (Native.IsIconic(hwnd))
            {
                SwitchToThisWindow(hwnd, true);
                Log.Info("restored a minimized window that refused a normal restore (running as administrator?)");
            }
        }

        // If the app has a modal dialog open, activate that instead (what Windows does).
        IntPtr popup = Native.GetLastActivePopup(hwnd);
        if (popup != IntPtr.Zero && Native.IsWindowVisible(popup)) hwnd = popup;

        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;

        // Windows' foreground lock refused us. Attaching to the current foreground thread's
        // input queue is the standard workaround.
        IntPtr foreground = Native.GetForegroundWindow();
        uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        uint thisThread = Native.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread &&
                        Native.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// Sends a press of an unassigned virtual key (0xE8) while Alt is held. Without it the app
    /// that had focus sees "Alt down, Alt up" with nothing in between (we swallowed Tab) and
    /// opens its menu bar. AutoHotkey uses the same "menu mask key" trick.
    /// </summary>
    public static void SendMenuMaskKey()
    {
        const ushort VK_UNASSIGNED = 0xE8;
        var inputs = new INPUT[2];
        inputs[0].type = Native.INPUT_KEYBOARD;
        inputs[0].U.ki = new KEYBDINPUT { wVk = VK_UNASSIGNED };
        inputs[1].type = Native.INPUT_KEYBOARD;
        inputs[1].U.ki = new KEYBDINPUT { wVk = VK_UNASSIGNED, dwFlags = Native.KEYEVENTF_KEYUP };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Documented, though "not intended for general use": what Windows' switcher calls to bring a window forward, restoring it if minimized.</summary>
    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);
}
