using System;
using System.Threading;
using System.Windows;

namespace AltTabEverywhere;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, "AltTabEverywhere.Settings", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SettingsWindow.ActivateExisting(); // already open: bring it to the front
            return 0;
        }

        // A window of controls: software rendering spares a Direct3D device and the GPU driver.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("settings window", e.Exception);
            e.Handled = true;
        };
        app.Startup += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Directory);
            var window = new SettingsWindow(SettingsStore.LoadOnce());
            app.MainWindow = window;
            window.Show();
            window.Activate();
        };
        return app.Run();
    }
}
