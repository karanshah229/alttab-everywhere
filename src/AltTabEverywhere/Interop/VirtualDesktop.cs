using System;

namespace AltTabEverywhere.Interop;

/// <summary>
/// Documented virtual-desktop API (Windows 10+): which desktop a window is on, through
/// IVirtualDesktopManager (slot 3: IsWindowOnCurrentVirtualDesktop; called by hand, see <see cref="Com"/>).
/// </summary>
internal static unsafe class VirtualDesktop
{
    private static readonly Guid ManagerClsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid ManagerIid = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");
    private static nint _manager;
    private static bool _unavailable;

    /// <summary>True if the window is on another virtual desktop; false if current or unknown.</summary>
    public static bool IsOnOtherDesktop(IntPtr hwnd)
    {
        nint manager = Manager();
        if (manager == 0) return false;
        int onCurrent;
        int hr = ((delegate* unmanaged[Stdcall]<nint, IntPtr, int*, int>)Com.Slot(manager, 3))(manager, hwnd, &onCurrent);
        if (hr == 0) return onCurrent == 0;
        if (hr is unchecked((int)0x800706BA) or unchecked((int)0x800706BE) or unchecked((int)0x80010108)) // RPC server gone, call failed, disconnected
        {
            Com.Release(_manager); // e.g. Explorer restarted; reconnect next time
            _manager = 0;
        }
        return false;
    }

    private static nint Manager()
    {
        if (_manager != 0 || _unavailable) return _manager;
        _manager = Com.Create(ManagerClsid, ManagerIid);
        if (_manager == 0) Log.Info("virtual desktop manager unavailable");
        _unavailable = _manager == 0;
        return _manager;
    }
}
