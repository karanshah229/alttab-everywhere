using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AltTabEverywhere.Interop;

/// <summary>The Show desktop tile's action: what Win+D does, through the shell's own ToggleDesktop.</summary>
internal static class ShellDesktop
{
    /// <summary>
    /// Shows the desktop (or, if it is already showing, brings the windows back: it's a toggle, so
    /// call it only when the desktop isn't what had focus; see <see cref="IsDesktopWindow"/>).
    /// </summary>
    /// <returns>False if the shell couldn't be asked.</returns>
    public static bool ToggleDesktop()
    {
        object? shell = null;
        try
        {
            // IShellDispatch4::ToggleDesktop (documented); late-bound, so nothing stays loaded between uses.
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return false;
            shell = Activator.CreateInstance(type);
            type.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
            return true;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException)
        {
            Log.Error("show desktop", ex);
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>The desktop itself: the shell's Progman window or a WorkerW that hosts the icons.</summary>
    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var name = new StringBuilder(16);
        if (GetClassName(hwnd, name, name.Capacity) == 0) return false;
        return name.ToString() is "Progman" or "WorkerW";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
}
