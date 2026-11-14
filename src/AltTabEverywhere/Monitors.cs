using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <param name="Scale">Effective DPI / 96 (1.0 = 100%, 1.5 = 150%).</param>
internal sealed record MonitorInfo(IntPtr Handle, string DeviceName, RECT Bounds, RECT WorkArea, bool IsPrimary, double Scale)
{
    /// <summary>Changes whenever the monitor layout changes, so overlay windows get rebuilt.</summary>
    public string Key => $"{Handle}|{DeviceName}|{Bounds}|{WorkArea}|{Scale}";
}

internal static class Monitors
{
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (Native.GetMonitorInfo(handle, ref info))
            {
                double scale = Native.GetDpiForMonitor(handle, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                    ? dpiX / 96.0
                    : 1.0;
                list.Add(new MonitorInfo(handle, info.szDevice, info.rcMonitor, info.rcWork,
                    (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, scale));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr UnderCursor()
    {
        Native.GetCursorPos(out var point);
        return Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST);
    }
}
