using System;
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
    public static unsafe bool ToggleDesktop()
    {
        // Shell.Application's IShellDispatch4::ToggleDesktop (documented), late-bound through
        // IDispatch by hand (see Com): created per use, so nothing stays loaded in between.
        nint shell = Com.Create(ShellApplicationClsid, DispatchIid, out int created);
        if (shell == 0)
        {
            Log.Info($"show desktop: the shell couldn't be asked (0x{created:X8})");
            return false;
        }
        try
        {
            Guid none = Guid.Empty;
            int dispId;
            fixed (char* name = "ToggleDesktop")
            {
                char* names = name;
                var getIdsOfNames = (delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint, uint, int*, int>)Com.Slot(shell, 5);
                if (getIdsOfNames(shell, &none, &names, 1, 0, &dispId) != 0) return false;
            }
            var parameters = new DISPPARAMS(); // no arguments
            var invoke = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DISPPARAMS*, void*, void*, uint*, int>)Com.Slot(shell, 6);
            int hr = invoke(shell, dispId, &none, 0, 1 /* DISPATCH_METHOD */, &parameters, null, null, null);
            if (hr != 0) Log.Info($"show desktop: ToggleDesktop failed (0x{hr:X8})");
            return hr == 0;
        }
        finally
        {
            Com.Release(shell);
        }
    }

    internal static readonly Guid ShellApplicationClsid = new("13709620-C279-11CE-A49E-444553540000");
    private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPPARAMS
    {
        public nint Arguments;
        public nint NamedArguments;
        public uint ArgumentCount;
        public uint NamedArgumentCount;
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
