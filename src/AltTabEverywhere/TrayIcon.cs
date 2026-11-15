using System;
using System.Diagnostics;
using System.IO;
using Forms = System.Windows.Forms;

namespace AltTabEverywhere;

/// <summary>Notification-area icon: the only way to pause or quit the app.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    private readonly Forms.ToolStripMenuItem _startWithWindows;

    public TrayIcon(SwitcherController controller, SettingsStore settings, Action exit)
    {
        var enabled = new Forms.ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => controller.Enabled = enabled.Checked;

        // Writes startWithWindows to settings.json; the settings reload applies the Run key.
        _startWithWindows = new Forms.ToolStripMenuItem("Start with Windows") { Checked = settings.Current.StartWithWindows };
        _startWithWindows.Click += (_, _) => SettingsStore.SetValue("startWithWindows", !settings.Current.StartWithWindows);
        settings.Changed += () => _startWithWindows.Checked = settings.Current.StartWithWindows;

        var openSettings = new Forms.ToolStripMenuItem("Settings…", null, (_, _) => SettingsWindow.Launch());
        openSettings.Font = new System.Drawing.Font(openSettings.Font, System.Drawing.FontStyle.Bold); // the double-click action
        var editSettings = new Forms.ToolStripMenuItem("Edit settings file…", null, (_, _) => OpenSettingsFile());

        var openLog = new Forms.ToolStripMenuItem("Open log folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
        });

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(openSettings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(enabled);
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(editSettings);
        menu.Items.Add(openLog);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AltTab Everywhere",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsWindow.Launch();
    }

    /// <summary>The app icon at the notification area's size for this DPI (16 px at 100%, 24 at 150%...).</summary>
    internal static System.Drawing.Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("AltTabEverywhere.app.ico");
        return stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
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
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
