using System;
using System.Runtime.InteropServices;

namespace AltTabEverywhere;

/// <summary>
/// An app or window icon as plain pixels, independent of any UI framework: 32-bit BGRA, premultiplied
/// alpha, top-down rows (what GDI's AlphaBlend and WPF's Pbgra32 both take). Immutable, so every
/// overlay thread can share it.
/// </summary>
internal sealed class AppIcon
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Width * Height * 4 bytes. Never modified after construction.</summary>
    public byte[] Pixels { get; }

    public AppIcon(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer does not match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Copies an icon handle's pixels (the handle stays the caller's). Icons with an alpha channel
    /// keep it; older ones without take their transparency from the mask. Null for monochrome icons
    /// or if the bitmaps can't be read.
    /// </summary>
    public static AppIcon? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero || !GetIconInfo(hIcon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null; // monochrome: the caller falls back to another icon
            if (ReadBitmap(info.hbmColor) is not { } color) return null;
            var (width, height, pixels) = color;

            if (HasAlpha(pixels))
            {
                Premultiply(pixels); // an icon's colour bitmap holds straight alpha
            }
            else
            {
                // No alpha: the AND mask says which pixels are transparent (white) or opaque (black).
                var mask = info.hbmMask != IntPtr.Zero ? ReadBitmap(info.hbmMask) : null;
                bool useMask = mask is { } m && m.Width == width && m.Height >= height;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    bool transparent = useMask && mask!.Value.Pixels[i] != 0;
                    if (transparent) pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                    else pixels[i + 3] = 255;
                }
            }
            return new AppIcon(width, height, pixels);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>
    /// Copies a 32-bit bitmap with straight (not premultiplied) alpha, as the shell's image factory
    /// returns icons. One with no alpha at all is shown opaque. The handle stays the caller's.
    /// </summary>
    public static AppIcon? FromBitmap(IntPtr bitmap)
    {
        if (ReadBitmap(bitmap) is not { } read) return null;
        var (width, height, pixels) = read;
        if (HasAlpha(pixels)) Premultiply(pixels);
        else for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new AppIcon(width, height, pixels);
    }

    /// <summary>The icon Explorer shows for a file (an exe's own icon). Null if it has none we can read.</summary>
    public static AppIcon? ForFile(string path)
    {
        if (!System.IO.File.Exists(path)) return null; // the shell would give a generic icon
        // The shell may write a different path back into the buffer (for files that use another's icon).
        var buffer = new char[Math.Max(path.Length + 1, 260)];
        path.CopyTo(0, buffer, 0, path.Length);
        ushort index = 0;
        IntPtr hIcon = ExtractAssociatedIcon(IntPtr.Zero, buffer, ref index);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            return FromHIcon(hIcon);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void Premultiply(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;
            pixels[i] = (byte)((pixels[i] * a + 127) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + 127) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + 127) / 255);
        }
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    /// <summary>Any bitmap's pixels as 32-bit top-down BGRA (GDI converts other depths).</summary>
    private static (int Width, int Height, byte[] Pixels)? ReadBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int width = bm.bmWidth, height = bm.bmHeight;
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
            return GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0 ? null : (width, height, pixels);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---- Interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractAssociatedIconW")]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr instance, [In, Out] char[] path, ref ushort index);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
