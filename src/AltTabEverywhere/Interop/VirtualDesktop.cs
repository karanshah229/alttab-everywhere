using System;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>Documented virtual-desktop API (Windows 10+): which desktop a window is on.</summary>
[ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

internal static class VirtualDesktop
{
    private static readonly Guid ManagerClsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static IVirtualDesktopManager? _manager;
    private static bool _unavailable;

    /// <summary>True if the window is on another virtual desktop; false if current or unknown.</summary>
    public static bool IsOnOtherDesktop(IntPtr hwnd)
    {
        var manager = Manager();
        if (manager is null) return false;
        try
        {
            return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out bool onCurrent) == 0 && !onCurrent;
        }
        catch (Exception)
        {
            _manager = null; // e.g. Explorer restarted; reconnect next time
            return false;
        }
    }

    private static IVirtualDesktopManager? Manager()
    {
        if (_manager is not null || _unavailable) return _manager;
        try
        {
            var type = Type.GetTypeFromCLSID(ManagerClsid, throwOnError: false);
            _manager = type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
        }
        catch (Exception ex)
        {
            Log.Error("virtual desktop manager", ex);
        }
        _unavailable = _manager is null;
        return _manager;
    }
}
