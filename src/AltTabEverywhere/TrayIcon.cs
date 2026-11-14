using System;
using System.Diagnostics;
using System.IO;
using Forms = System.Windows.Forms;

namespace AltTabEverywhere;

/// <summary>Notification-area icon: the only way to pause or quit the app.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(SwitcherController controller, Action exit)
    {
        var enabled = new Forms.ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => controller.Enabled = enabled.Checked;

        var openLog = new Forms.ToolStripMenuItem("Open log folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
        });

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(enabled);
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
