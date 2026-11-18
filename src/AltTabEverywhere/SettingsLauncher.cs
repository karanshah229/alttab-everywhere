using System;
using System.IO;

namespace AltTabEverywhere;

/// <summary>
/// Opens the settings window: AltTabEverywhere.Settings.exe, next to this exe, a WPF app of its own.
/// The switcher process never loads the window's controls, styles or code (nor WPF), and closing
/// the window gives everything back.
/// </summary>
internal static class SettingsLauncher
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "AltTabEverywhere.Settings.exe");

    public static void Launch()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error($"open settings window ({ExePath})", ex);
        }
    }
}
