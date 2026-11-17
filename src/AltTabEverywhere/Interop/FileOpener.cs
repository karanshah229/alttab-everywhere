using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AltTabEverywhere.Interop;

/// <summary>
/// dropFilesOnTiles: opens dropped files in the app a tile belongs to, as macOS's Cmd+Tab does.
/// Desktop apps get the files as arguments to their own exe (most apps hand them to their running
/// window); Store and packaged apps get them through Windows' activation API.
/// </summary>
internal static class FileOpener
{
    internal enum How
    {
        /// <summary>This app can't take files: File Explorer, Back, Show desktop, unknown exe, this app itself.</summary>
        NotSupported,
        /// <summary>Run the window's exe with the files as arguments.</summary>
        Exe,
        /// <summary>A packaged (Store) app: IApplicationActivationManager.ActivateForFile.</summary>
        PackagedApp,
    }

    internal sealed record Plan(How How, string? Target);

    /// <summary>How files dropped on a window's tile would be opened. Pure, for testing.</summary>
    /// <param name="exePath">The window's process image, or null if it can't be read.</param>
    /// <param name="appUserModelId">The window's shell app ID, if any.</param>
    /// <param name="frameHosted">A Store app hosted by ApplicationFrameHost (its exe is the host, not the app).</param>
    /// <param name="ownExePath">This app's exe: dropping on its own windows (the settings window) does nothing.</param>
    internal static Plan PlanFor(string? exePath, string? appUserModelId, bool frameHosted, string? ownExePath)
    {
        string exeName = exePath is null ? "" : Path.GetFileName(exePath);
        // Explorer would open the file in its default app, not do anything with "this" window.
        if (exeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return new Plan(How.NotSupported, null);
        if (exePath is not null && ownExePath is not null && string.Equals(exePath, ownExePath, StringComparison.OrdinalIgnoreCase))
            return new Plan(How.NotSupported, null);
        bool packagedExe = exePath is not null && exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        if (AppIcons.IsPackagedAppId(appUserModelId) && (frameHosted || packagedExe || exePath is null))
            return new Plan(How.PackagedApp, appUserModelId);
        // A packaged app whose ID we don't know: its exe can't be started directly.
        if (packagedExe || frameHosted || exePath is null) return new Plan(How.NotSupported, null);
        return new Plan(How.Exe, exePath);
    }

    /// <summary>The plan for a real window: reads its exe and host.</summary>
    public static Plan PlanFor(WindowInfo window) =>
        PlanFor(WindowEnumerator.ProcessPathOf(window), window.AppUserModelId, AppIcons.IsFrameHosted(window.Handle), Environment.ProcessPath);

    /// <summary>Opens the files the plan's way. Returns false (and logs why) if it couldn't.</summary>
    public static bool Open(Plan plan, IReadOnlyList<string> files)
    {
        if (files.Count == 0 || plan.Target is null) return false;
        try
        {
            switch (plan.How)
            {
                case How.Exe:
                    Process.Start(new ProcessStartInfo(plan.Target, Arguments(files))
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(files[0]) ?? "",
                    })?.Dispose();
                    return true;
                case How.PackagedApp:
                    var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                    try
                    {
                        // One file at a time: every packaged app accepts a single item; some ignore extras.
                        foreach (string file in files)
                        {
                            SHCreateItemFromParsingName(file, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem item);
                            SHCreateShellItemArrayFromShellItem(item, typeof(IShellItemArray).GUID, out IShellItemArray array);
                            int hr = manager.ActivateForFile(plan.Target, array, "open", out _);
                            Marshal.ReleaseComObject(array);
                            Marshal.ReleaseComObject(item);
                            Marshal.ThrowExceptionForHR(hr);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(manager);
                    }
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is COMException or System.ComponentModel.Win32Exception or InvalidCastException or FileNotFoundException)
        {
            Log.Info($"drop: couldn't open {files.Count} file(s) in the app ({ex.GetType().Name}: 0x{ex.HResult:X8})");
            return false;
        }
    }

    /// <summary>
    /// A command line that hands each path over exactly (CommandLineToArgvW rules): quoted, with
    /// backslashes before a quote, and at the end, doubled.
    /// </summary>
    internal static string Arguments(IEnumerable<string> paths) => string.Join(" ", paths.Select(Quote));

    private static string Quote(string arg)
    {
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    // ---- Interop ----

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IShellItemArray itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IShellItemArray itemArray, out uint processId);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray { }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromShellItem(IShellItem item, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItemArray array);
}
