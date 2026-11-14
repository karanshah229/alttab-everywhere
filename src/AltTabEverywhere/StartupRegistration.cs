using System;
using Microsoft.Win32;

namespace AltTabEverywhere;

/// <summary>
/// "Start with Windows" via the per-user Run key (HKCU\...\Run). No admin rights, and Windows'
/// Settings > Apps > Startup page can switch it off too.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AltTabEverywhere";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            string? current = key.GetValue(ValueName) as string;
            string wanted = $"\"{Environment.ProcessPath}\"";

            if (enabled && !string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, wanted);
                Log.Info("start with Windows: enabled");
            }
            else if (!enabled && current is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("start with Windows: disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Error("start with Windows", ex);
        }
    }
}
