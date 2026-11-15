using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

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
    public static ImageSource? ForAppId(string appUserModelId)
    {
        if (Cache.TryGetValue(appUserModelId, out var cached)) return cached;
        if (Cache.Count >= MaxCached) Cache.Clear();

        ImageSource? icon = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var appsFolder = FolderIdAppsFolder;
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemInKnownFolder(ref appsFolder, 0, appUserModelId, ref iid, out object item);
            var factory = (IShellItemImageFactory)item;
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_ICONONLY, out bitmap) == 0)
                icon = ToImage(bitmap);
            Marshal.ReleaseComObject(item);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // Not an installed app (or not one the shell can draw): use the window's icon.
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }

        Cache[appUserModelId] = icon;
        return icon;
    }

    /// <summary>Copies a 32-bit HBITMAP (premultiplied alpha, as the shell draws icons) into a frozen image.</summary>
    private static ImageSource? ToImage(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0) return null;
        int width = info.bmWidth, height = info.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        // An icon without an alpha channel comes back fully transparent: show it opaque instead.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var image = BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * 4);
        image.Freeze(); // shared with the overlays' threads
        return image;
    }

    // ---- Interop ----

    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private const int SIIGBF_ICONONLY = 0x4;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemInKnownFolder(ref Guid folderId, uint flags, string item, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object result);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
