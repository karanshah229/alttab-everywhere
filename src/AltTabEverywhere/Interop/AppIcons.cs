using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>
/// Icons of packaged (Store) apps, as the Start menu shows them. Their windows are often hosted by
/// ApplicationFrameHost.exe, so asking the window or its exe gives a generic icon; the shell's
/// Apps folder knows the real one from the package's logo. Looked up by app ID
/// ("PackageFamilyName!AppId"), once per app, and cached.
/// </summary>
internal static class AppIcons
{
    /// <summary>Pixels requested: sharp for a 48-DIP icon up to 200% scaling.</summary>
    private const int Size = 96;
    private const int MaxCached = 256;

    private static readonly Dictionary<string, AppIcon?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A packaged app's ID has the form "PackageFamilyName!AppId".</summary>
    public static bool IsPackagedAppId(string? appUserModelId) =>
        appUserModelId is { Length: > 2 } id && id.IndexOf('!') > 0 && id.IndexOf('!') < id.Length - 1;

    /// <summary>
    /// A Store (UWP) app's window: hosted by ApplicationFrameHost, so its own icon is the host's
    /// generic one. Only these need the lookup; packaged desktop apps (Outlook, Terminal...) give
    /// good icons themselves, and skipping them means the shell's app catalogue (a one-time ~170
    /// handles and ~3 MB) is only loaded when a Store app window is actually open.
    /// </summary>
    public static bool IsFrameHosted(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(32);
        return GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "ApplicationFrameWindow";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    /// <summary>The app's icon, or null if the shell doesn't know it (the caller falls back to the window's).</summary>
    public static unsafe AppIcon? ForAppId(string appUserModelId)
    {
        if (Cache.TryGetValue(appUserModelId, out var cached)) return cached;
        if (Cache.Count >= MaxCached) Cache.Clear();

        AppIcon? icon = null;
        IntPtr bitmap = IntPtr.Zero;
        nint factory = 0;
        try
        {
            // Not an installed app (or not one the shell can draw): no item, and the window's icon is used.
            Guid appsFolder = FolderIdAppsFolder, iid = ImageFactoryIid;
            fixed (char* id = appUserModelId)
                if (SHCreateItemInKnownFolder(&appsFolder, 0, id, &iid, &factory) != 0 || factory == 0) factory = 0;
            // IShellItemImageFactory, slot 3: GetImage(SIZE, flags, HBITMAP*). Called by hand (see Com).
            if (factory != 0 && ((delegate* unmanaged[Stdcall]<nint, SIZE, int, IntPtr*, int>)Com.Slot(factory, 3))(
                    factory, new SIZE { cx = Size, cy = Size }, SIIGBF_ICONONLY, &bitmap) == 0)
                icon = AppIcon.FromBitmap(bitmap);
        }
        finally
        {
            Com.Release(factory);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }

        Cache[appUserModelId] = icon;
        return icon;
    }

    // ---- Interop ----

    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid ImageFactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("shell32.dll")]
    private static extern unsafe int SHCreateItemInKnownFolder(Guid* folderId, uint flags, char* item, Guid* iid, nint* result);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
