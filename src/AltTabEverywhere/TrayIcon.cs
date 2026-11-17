using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AltTabEverywhere.Interop;
using static AltTabEverywhere.Interop.Win32;

namespace AltTabEverywhere;

/// <summary>
/// Notification-area icon: the only way to pause or quit the app. Plain Shell_NotifyIcon and a popup
/// menu built when it's opened (nothing is kept for it in between).
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8002; // WM_APP + 2
    private const uint IconId = 1;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x1;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;

    private enum Command { None, Download, Settings, Enabled, StartWithWindows, EditSettings, OpenLog, Exit }

    private readonly SwitcherController _controller;
    private readonly SettingsStore _settings;
    private readonly Action _exit;
    private readonly HiddenWindow _window;
    private readonly int _taskbarCreated;
    private IntPtr _icon;
    private UpdateInfo? _update;
    private string _tip = $"AltTab Everywhere {AppInfo.Version}";

    public TrayIcon(SwitcherController controller, SettingsStore settings, Action exit)
    {
        _controller = controller;
        _settings = settings;
        _exit = exit;
        fixed (char* name = "TaskbarCreated") _taskbarCreated = (int)RegisterWindowMessageW(name);
        _window = new HiddenWindow("AltTabEverywhere.Tray", WndProc);
        _icon = LoadAppIcon();
        Add();
    }

    /// <summary>
    /// A newer version exists: a "Download version X…" item at the top of the menu, and (when it was
    /// only just found) a notification. Both open the release page on GitHub.
    /// </summary>
    public void ShowUpdate(UpdateInfo update, bool notify)
    {
        if (!update.Url.StartsWith(UpdateCheck.ReleasesPrefix, StringComparison.OrdinalIgnoreCase)) return;
        _update = update;
        _tip = $"AltTab Everywhere {AppInfo.Version} (version {update.Version} available)";
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        if (notify)
        {
            data.uFlags |= NIF_INFO;
            Copy("AltTab Everywhere", data.szInfoTitle, 64);
            Copy($"Version {update.Version} is available. Click to open its page.", data.szInfo, 256);
            data.dwInfoFlags = NIIF_INFO;
        }
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (Shell_NotifyIconW(NIM_ADD, &data) == 0) Log.Info("tray: could not add the icon (the notification area may not be ready yet)");
        else Log.Info("tray: icon added");
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4; // richer callbacks: a right-click comes as WM_CONTEXTMENU with its position
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private NOTIFYICONDATAW Data(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        Copy(_tip, data.szTip, 128);
        return data;
    }

    private static void Copy(string text, char* target, int capacity)
    {
        int length = Math.Min(text.Length, capacity - 1);
        for (int i = 0; i < length; i++) target[i] = text[i];
        target[length] = '\0';
    }

    private bool WndProc(uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg == CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WM_CONTEXTMENU:
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
                case WM_LBUTTONDBLCLK:
                case NIN_KEYSELECT:
                    SettingsLauncher.Launch();
                    break;
                case NIN_BALLOONUSERCLICK:
                    OpenUpdatePage();
                    break;
            }
            return true;
        }
        if (_taskbarCreated != 0 && msg == (uint)_taskbarCreated)
        {
            Add(); // Explorer restarted: the notification area is new and empty
            return true;
        }
        return false;
    }

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            void Item(Command command, string text, bool isChecked = false)
            {
                fixed (char* p = text) AppendMenuW(menu, MF_STRING | (isChecked ? MF_CHECKED : 0), (nuint)command, p);
            }
            void Separator() => AppendMenuW(menu, MF_SEPARATOR, 0, null);

            if (_update is not null) Item(Command.Download, $"Download version {_update.Version}…");
            Item(Command.Settings, "Settings…");
            SetMenuDefaultItem(menu, (uint)Command.Settings, 0); // bold: what a double-click does
            Separator();
            Item(Command.Enabled, "Enabled", _controller.Enabled);
            Item(Command.StartWithWindows, "Start with Windows", _settings.Current.StartWithWindows);
            Item(Command.EditSettings, "Edit settings file…");
            Item(Command.OpenLog, "Open log folder");
            Separator();
            Item(Command.Exit, "Exit");

            ThemeMenus(Theme.Current.IsDark);
            SetForegroundWindow(_window.Handle); // so a click elsewhere closes the menu
            var chosen = (Command)TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, _window.Handle, IntPtr.Zero);
            PostMessageW(_window.Handle, 0 /* WM_NULL */, 0, 0);
            Run(chosen);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Run(Command command)
    {
        switch (command)
        {
            case Command.Download:
                OpenUpdatePage();
                break;
            case Command.Settings:
                SettingsLauncher.Launch();
                break;
            case Command.Enabled:
                _controller.Enabled = !_controller.Enabled;
                break;
            case Command.StartWithWindows:
                // Writes startWithWindows to settings.json; the settings reload applies the Run key.
                SettingsStore.SetValue("startWithWindows", !_settings.Current.StartWithWindows);
                break;
            case Command.EditSettings:
                OpenSettingsFile();
                break;
            case Command.OpenLog:
                Directory.CreateDirectory(Log.Directory);
                Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
                break;
            case Command.Exit:
                _exit();
                break;
        }
    }

    private void OpenUpdatePage()
    {
        if (_update is { } update) Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true });
    }

    /// <summary>The app's icon at the notification area's size for this DPI (16 px at 100%, 24 at 150%...).</summary>
    private static IntPtr LoadAppIcon()
    {
        IntPtr icon;
        // The exe's own icon (ApplicationIcon), which the .NET build stores as resource 32512.
        if (LoadIconMetric(GetModuleHandleW(null), 32512, 0 /* LIM_SMALL */, &icon) == 0) return icon;
        string? exe = Environment.ProcessPath;
        if (exe is not null)
        {
            IntPtr small = IntPtr.Zero;
            fixed (char* path = exe)
                if (ExtractIconExW(path, 0, null, &small, 1) > 0 && small != IntPtr.Zero) return small;
        }
        return LoadIconW(IntPtr.Zero, 32512); // IDI_APPLICATION
    }

    /// <summary>Opens settings.json in the default .json editor, or Notepad if none is associated.</summary>
    internal static void OpenSettingsFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SettingsStore.FilePath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SettingsStore.FilePath}\"") { UseShellExecute = true });
        }
    }

    public void Dispose()
    {
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, &data);
        _window.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [DllImport("shell32.dll")] private static extern uint ExtractIconExW(char* file, int index, IntPtr* large, IntPtr* small, uint count);
    [DllImport("comctl32.dll")] private static extern int LoadIconMetric(IntPtr instance, nint name, int metric, IntPtr* icon);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr instance, nint name);
    [DllImport("user32.dll")] private static extern int DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern uint RegisterWindowMessageW(char* name);
}
