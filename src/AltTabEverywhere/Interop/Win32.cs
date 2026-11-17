using System;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>
/// Window, GDI and GDI+ calls for the Win32 switcher. Every signature is blittable (pointers, ints,
/// handles), so no marshalling code is generated and they work unchanged under NativeAOT.
/// </summary>
internal static unsafe class Win32
{
    // ---- Messages ----
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
        WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_GETOBJECT = 0x003D,
        WM_NOTIFY = 0x004E, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_CAPTURECHANGED = 0x0215,
        WM_MOUSELEAVE = 0x02A3, WM_DPICHANGED = 0x02E0;
    public const int WA_INACTIVE = 0;
    public const int MK_CONTROL = 0x0008;

    // ---- Window styles ----
    public const uint WS_POPUP = 0x80000000, WS_CLIPCHILDREN = 0x02000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
    public const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
    public const int GWLP_USERDATA = -21;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
        SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    public const int IDC_ARROW = 32512, IDC_HAND = 32649;
    public const uint TME_LEAVE = 0x00000002;

    // ---- Menus ----
    public const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080;

    // ---- GDI ----
    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020;
    public const uint DT_CENTER = 0x0001, DT_RIGHT = 0x0002, DT_VCENTER = 0x0004, DT_SINGLELINE = 0x0020,
        DT_NOCLIP = 0x0100, DT_CALCRECT = 0x0400, DT_NOPREFIX = 0x0800, DT_END_ELLIPSIS = 0x8000;
    public const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
    public const uint CLEARTYPE_QUALITY = 5, DEFAULT_CHARSET = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
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

    // ---- user32 ----
    [DllImport("user32.dll")] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] public static extern IntPtr CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern int DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] public static extern int ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern int IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int InvalidateRect(IntPtr hwnd, RECT* rect, int erase);
    [DllImport("user32.dll")] public static extern int UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr hwnd, PAINTSTRUCT* ps);
    [DllImport("user32.dll")] public static extern int EndPaint(IntPtr hwnd, PAINTSTRUCT* ps);
    [DllImport("user32.dll")] public static extern int GetClientRect(IntPtr hwnd, RECT* rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int TrackMouseEvent(TRACKMOUSEEVENT* tme);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseCapture();
    [DllImport("user32.dll")] public static extern int ScreenToClient(IntPtr hwnd, POINT* point);
    [DllImport("user32.dll")] public static extern int GetCursorPos(POINT* point);
    [DllImport("user32.dll")] public static extern int SystemParametersInfoW(uint action, uint param, void* value, uint winIni);
    [DllImport("user32.dll")] public static extern int DrawTextW(IntPtr dc, char* text, int length, RECT* rect, uint format);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, RECT* rect, IntPtr brush);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern int AppendMenuW(IntPtr menu, uint flags, nuint id, char* text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern int DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern int EndMenu();
    [DllImport("user32.dll")] public static extern int SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(char* name);

    // ---- Tooltips (comctl32) ----
    public const uint TTS_ALWAYSTIP = 0x01, TTS_NOPREFIX = 0x02, TTF_SUBCLASS = 0x0010;
    public const uint TTM_POP = 0x041C, TTM_ADDTOOLW = 0x0432, TTM_NEWTOOLRECTW = 0x0434, TTM_UPDATETIPTEXTW = 0x0439;
    public const uint ICC_BAR_CLASSES = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct TOOLINFOW
    {
        public uint cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public nuint uId;
        public RECT rect;
        public IntPtr hinst;
        public char* lpszText;
        public nint lParam;
        public IntPtr lpReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    [DllImport("comctl32.dll")] public static extern int InitCommonControlsEx(INITCOMMONCONTROLSEX* icc);
    [DllImport("user32.dll")] public static extern nint SendMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    // ---- Message loop ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll")] public static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern int PeekMessageW(MSG* msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern int TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] public static extern int PostMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern nuint SetTimer(IntPtr hwnd, nuint id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] public static extern int KillTimer(IntPtr hwnd, nuint id);

    // ---- gdi32 ----
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern int DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, BITMAPINFOHEADER* info, uint usage, void** bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern int DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern int BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] public static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern int SelectClipRgn(IntPtr dc, IntPtr region);
    [DllImport("gdi32.dll")] public static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern int RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")] public static extern int GetTextFaceW(IntPtr dc, int count, char* face);
    [DllImport("gdiplus.dll")] public static extern int GdipGetDC(IntPtr graphics, IntPtr* dc);
    [DllImport("gdiplus.dll")] public static extern int GdipReleaseDC(IntPtr graphics, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern int GetTextExtentPoint32W(IntPtr dc, char* text, int length, SIZE* size);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);

    // ---- uxtheme (undocumented, ordinal only): dark or light popup menus to match the theme ----
    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    /// <summary>Popup menus dark or light, to match the theme (process-wide; undocumented, so best effort).</summary>
    public static void ThemeMenus(bool dark)
    {
        try
        {
            SetPreferredAppMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older Windows: menus keep the system's look.
        }
    }

    // ---- gdiplus ----
    [StructLayout(LayoutKind.Sequential)]
    public struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    public const int PixelFormat32bppPARGB = 0x000E200B;
    public const int SmoothingModeAntiAlias = 4;
    public const int InterpolationModeHighQualityBicubic = 7;
    public const int PixelOffsetModeHalf = 4;
    public const int UnitPixel = 2;

    [DllImport("gdiplus.dll")] public static extern int GdiplusStartup(IntPtr* token, GdiplusStartupInput* input, IntPtr output);
    [DllImport("gdiplus.dll")] public static extern int GdipCreateFromHDC(IntPtr dc, IntPtr* graphics);
    [DllImport("gdiplus.dll")] public static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport("gdiplus.dll")] public static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] public static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] public static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] public static extern int GdipSetClipRectI(IntPtr graphics, int x, int y, int w, int h, int combineMode);
    [DllImport("gdiplus.dll")] public static extern int GdipResetClip(IntPtr graphics);
    [DllImport("gdiplus.dll")] public static extern int GdipCreatePath(int fillMode, IntPtr* path);
    [DllImport("gdiplus.dll")] public static extern int GdipDeletePath(IntPtr path);
    [DllImport("gdiplus.dll")] public static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
    [DllImport("gdiplus.dll")] public static extern int GdipClosePathFigure(IntPtr path);
    [DllImport("gdiplus.dll")] public static extern int GdipCreateSolidFill(uint argb, IntPtr* brush);
    [DllImport("gdiplus.dll")] public static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport("gdiplus.dll")] public static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);
    [DllImport("gdiplus.dll")] public static extern int GdipFillRectangle(IntPtr graphics, IntPtr brush, float x, float y, float w, float h);
    [DllImport("gdiplus.dll")] public static extern int GdipCreatePen1(uint argb, float width, int unit, IntPtr* pen);
    [DllImport("gdiplus.dll")] public static extern int GdipDeletePen(IntPtr pen);
    [DllImport("gdiplus.dll")] public static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);
    [DllImport("gdiplus.dll")] public static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, byte* scan0, IntPtr* bitmap);
    [DllImport("gdiplus.dll")] public static extern int GdipDisposeImage(IntPtr image);
    [DllImport("gdiplus.dll")] public static extern int GdipDrawImageRectRect(IntPtr graphics, IntPtr image, float dx, float dy, float dw, float dh,
        float sx, float sy, float sw, float sh, int unit, IntPtr attributes, IntPtr callback, IntPtr callbackData);
    [DllImport("gdiplus.dll")] public static extern int GdipCreateImageAttributes(IntPtr* attributes);
    [DllImport("gdiplus.dll")] public static extern int GdipDisposeImageAttributes(IntPtr attributes);
    [DllImport("gdiplus.dll")] public static extern int GdipSetImageAttributesColorMatrix(IntPtr attributes, int type, int enable, float* matrix, float* grayMatrix, int flags);
}
