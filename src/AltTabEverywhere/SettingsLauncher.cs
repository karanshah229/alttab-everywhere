using System;

namespace AltTabEverywhere;

/// <summary>
/// Opens the settings window in its own short-lived process (this exe with --settings). The
/// switcher process never loads the window's controls, styles or code (nor WPF), so closing the
/// window gives everything back (in-process, about 22 MB of private memory stayed behind).
/// </summary>
internal static class SettingsLauncher
{
    public static void Launch()
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--settings") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("open settings window", ex);
        }
    }
}
