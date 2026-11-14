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

        var editSettings = new Forms.ToolStripMenuItem("Edit settings…", null, (_, _) => OpenSettingsFile());

        var openLog = new Forms.ToolStripMenuItem("Open log folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
        });

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(enabled);
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(editSettings);
        menu.Items.Add(openLog);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "AltTab Everywhere",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>Opens settings.json in the default .json editor, or Notepad if none is associated.</summary>
    private static void OpenSettingsFile()
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
        _icon.Dispose();
    }
}
