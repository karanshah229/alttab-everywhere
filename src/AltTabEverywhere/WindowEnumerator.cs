using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <param name="AppUserModelId">The shell's app ID (null with the fallback filter). Basis for grouping windows by app.</param>
/// <param name="OnOtherDesktop">On another virtual desktop (only listed with virtualDesktops = all).</param>
/// <param name="IsElevated">Runs as administrator (only checked when the admin badge is on).</param>
/// <param name="IsHung">Not responding (only checked when that badge is on).</param>
/// <param name="IsTopmost">Kept above other windows (always on top).</param>
internal sealed record WindowInfo(IntPtr Handle, string Title, ImageSource? Icon, IntPtr Monitor, bool IsMinimized,
    string? AppUserModelId, bool OnOtherDesktop = false, bool IsElevated = false, bool IsHung = false, bool IsTopmost = false);

/// <summary>What <see cref="WindowEnumerator.GetSwitchableWindows"/> lists and finds out about each window.</summary>
/// <param name="IncludeOtherDesktops">Also list windows on other virtual desktops.</param>
/// <param name="PackagedAppIcons">Store apps get their package's icon (storeAppIcons) instead of the window's.</param>
/// <param name="Elevation">Check whether each window's process runs as administrator.</param>
/// <param name="Hung">Check whether each window is not responding.</param>
internal sealed record WindowQuery(bool IncludeOtherDesktops = false, bool PackagedAppIcons = true,
    bool Elevation = false, bool Hung = false);

/// <summary>Lists the windows that belong in Alt+Tab, in most-recently-used order.</summary>
internal static class WindowEnumerator
{
    private static readonly Dictionary<IntPtr, ImageSource> HIconCache = new();
    private static readonly Dictionary<string, ImageSource?> ExeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ShellViews Shell = new();
    private static string? _lastSource;
    private static WindowQuery _query = new();
    /// <summary>Elevation per process during one listing (a process's windows share it); emptied after.</summary>
    private static readonly Dictionary<uint, bool> ElevatedPids = new();

    public static List<WindowInfo> GetSwitchableWindows(WindowQuery? query = null)
    {
        if (HIconCache.Count > 512) HIconCache.Clear();
        _query = query ?? new WindowQuery();
        bool includeOtherDesktops = _query.IncludeOtherDesktops;

        // Preferred: the shell's own Alt+Tab list (exact match with Windows). Fallback: window-style
        // heuristics, used if the undocumented interfaces are missing on this Windows build.
        var fromShell = FromShell(includeOtherDesktops);
        string source = fromShell is null ? "heuristic filter" : "shell view list";
        if (source != _lastSource)
        {
            Log.Info($"window list source: {source}");
            _lastSource = source;
        }
        var windows = fromShell ?? FromHeuristics(includeOtherDesktops);
        ElevatedPids.Clear();
        return windows;
    }

    /// <summary>The window's executable path, or null (e.g. protected processes).</summary>
    public static string? ProcessPathOf(WindowInfo window) => GetProcessPath(window.Handle);

    private const int DWM_CLOAKED_SHELL = 0x2;

    /// <summary>
    /// Whether a cloaked window should still be listed: only when other desktops are wanted and the
    /// shell cloaked it because it lives on another virtual desktop. Sets <paramref name="otherDesktop"/>.
    /// </summary>
    private static bool PassesCloakCheck(IntPtr hwnd, bool includeOtherDesktops, out bool otherDesktop)
    {
        otherDesktop = false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0)
            return true;
        if (!includeOtherDesktops || (cloaked & DWM_CLOAKED_SHELL) == 0 || !VirtualDesktop.IsOnOtherDesktop(hwnd))
            return false;
        otherDesktop = true;
        return true;
    }

    /// <summary>--list: both sources side by side, for checking the filters against Windows' Alt+Tab.</summary>
    public static IEnumerable<string> DescribeSources()
    {
        _query = new WindowQuery(IncludeOtherDesktops: true, Elevation: true, Hung: true);
        var shell = FromShell(includeOtherDesktops: true);
        ElevatedPids.Clear();
        yield return shell is null ? "== shell view list: UNAVAILABLE" : $"== shell view list ({shell.Count})";
        foreach (var w in shell ?? new List<WindowInfo>())
        {
            Native.GetWindowThreadProcessId(w.Handle, out uint pid);
            var states = StateBadges.For(w, new Settings()).Select(b => b.Label.ToLowerInvariant()).ToList();
            yield return $"  {w.Title}  [{w.AppUserModelId}] pid {pid}{(states.Count > 0 ? "  (" + string.Join(", ", states) + ")" : "")}";
        }

        var heuristic = FromHeuristics(includeOtherDesktops: false);
        yield return $"== heuristic filter ({heuristic.Count})";
        foreach (var w in heuristic) yield return $"  {w.Title}";
    }

    private static List<WindowInfo>? FromShell(bool includeOtherDesktops)
    {
        var views = Shell.GetSwitcherViews();
        if (views is null) return null;

        var result = new List<WindowInfo>(views.Count);
        uint myPid = (uint)Environment.ProcessId;
        foreach (var view in views)
        {
            try
            {
                if (!Native.IsWindowVisible(view.Hwnd)) continue;
                Native.GetWindowThreadProcessId(view.Hwnd, out uint pid);
                if (pid == myPid) continue;
                // Windows on other virtual desktops are cloaked; listed only if asked for.
                if (!PassesCloakCheck(view.Hwnd, includeOtherDesktops, out bool otherDesktop)) continue;
                result.Add(Describe(view.Hwnd, view.AppUserModelId, otherDesktop));
            }
            catch (Exception ex)
            {
                Log.Error("describe shell view", ex);
            }
        }
        return result;
    }

    private static List<WindowInfo> FromHeuristics(bool includeOtherDesktops)
    {
        var result = new List<WindowInfo>();
        uint myPid = (uint)Environment.ProcessId;
        IntPtr shellWindow = Native.GetShellWindow();

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (IsAltTabWindow(hwnd, shellWindow, myPid, includeOtherDesktops, out bool otherDesktop))
                    result.Add(Describe(hwnd, null, otherDesktop));
            }
            catch (Exception ex)
            {
                Log.Error("enumerate window", ex);
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static bool IsAltTabWindow(IntPtr hwnd, IntPtr shellWindow, uint myPid, bool includeOtherDesktops, out bool otherDesktop)
    {
        otherDesktop = false;
        if (hwnd == shellWindow || !Native.IsWindowVisible(hwnd)) return false;
        if (Native.GetWindowTextLength(hwnd) == 0) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == myPid) return false;

        long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (exStyle & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow && (exStyle & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return false;

        // Cloaked = hidden by DWM: suspended Store apps, windows on other virtual desktops, etc.
        if (!PassesCloakCheck(hwnd, includeOtherDesktops, out otherDesktop)) return false;

        if (!appWindow)
        {
            // Raymond Chen's rule: an app is represented by its root owner, unless that owner
            // has a visible last-active popup. Owned dialogs are therefore not listed separately.
            IntPtr walk = IntPtr.Zero;
            IntPtr candidate = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
            while (candidate != walk)
            {
                walk = candidate;
                candidate = Native.GetLastActivePopup(walk);
                if (Native.IsWindowVisible(candidate)) break;
            }
            if (walk != hwnd) return false;
        }

        return true;
    }

    private static WindowInfo Describe(IntPtr hwnd, string? appUserModelId, bool otherDesktop)
    {
        int length = Native.GetWindowTextLength(hwnd);
        var title = new StringBuilder(length + 1);
        Native.GetWindowText(hwnd, title, title.Capacity);

        return new WindowInfo(
            hwnd,
            title.ToString(),
            (_query.PackagedAppIcons && AppIcons.IsPackagedAppId(appUserModelId) && AppIcons.IsFrameHosted(hwnd) ? AppIcons.ForAppId(appUserModelId!) : null) ?? GetIcon(hwnd),
            Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST),
            Native.IsIconic(hwnd),
            appUserModelId,
            otherDesktop,
            _query.Elevation && IsElevated(hwnd),
            _query.Hung && Native.IsHungAppWindow(hwnd),
            WindowActions.IsTopmost(hwnd));
    }

    private static bool IsElevated(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!ElevatedPids.TryGetValue(pid, out bool elevated))
            ElevatedPids[pid] = elevated = ProcessElevation.IsElevated(pid);
        return elevated;
    }

    private static ImageSource? GetIcon(IntPtr hwnd)
    {
        IntPtr hIcon = IntPtr.Zero;
        foreach (int type in new[] { Native.ICON_BIG, Native.ICON_SMALL2, Native.ICON_SMALL })
        {
            // Short timeout + ABORTIFHUNG so one frozen app can't stall the switcher.
            if (Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)type, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 30, out hIcon) != IntPtr.Zero && hIcon != IntPtr.Zero)
                break;
            hIcon = IntPtr.Zero;
        }
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);

        if (hIcon != IntPtr.Zero)
        {
            if (HIconCache.TryGetValue(hIcon, out var cached)) return cached;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                HIconCache[hIcon] = source;
                return source;
            }
            catch
            {
                // Fall through to the exe icon.
            }
        }

        return GetExeIcon(hwnd);
    }

    private static ImageSource? GetExeIcon(IntPtr hwnd)
    {
        string? path = GetProcessPath(hwnd);
        if (path is null) return null;
        if (ExeIconCache.TryGetValue(path, out var cached)) return cached;

        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                source = bitmap;
            }
        }
        catch
        {
            // Protected processes etc. — no icon.
        }

        ExeIconCache[path] = source;
        return source;
    }

    /// <summary>
    /// Which app a window belongs to, for stacking: the shell's app ID (keeps e.g. Chrome profiles
    /// apart), or the executable path when there is none (fallback window list).
    /// </summary>
    /// <summary>When the window's process started (UTC), or null if it can't be read. For --demo-since.</summary>
    public static DateTime? ProcessStartUtc(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(process, out long created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>sortOrder = stacking: each top-level window's place front to back (EnumWindows walks the z-order).</summary>
    public static Dictionary<IntPtr, int> StackingOrder()
    {
        var order = new Dictionary<IntPtr, int>();
        Native.EnumWindows((hwnd, _) =>
        {
            order.TryAdd(hwnd, order.Count);
            return true;
        }, IntPtr.Zero);
        return order;
    }

    public static string AppKey(WindowInfo window) =>
        window.AppUserModelId ?? "exe:" + (GetProcessPath(window.Handle) ?? window.Handle.ToString());

    private static string? GetProcessPath(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}
