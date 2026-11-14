using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AltTabEverywhere;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        bool selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        demo |= selfTest; // same single-instance rules: never touches the keyboard hook

        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            // Writes window titles, so it goes to its own file that only this flag creates.
            string path = System.IO.Path.Combine(Log.Directory, "windows.txt");
            System.IO.Directory.CreateDirectory(Log.Directory);
            System.IO.File.WriteAllLines(path, WindowEnumerator.DescribeSources());
            return 0;
        }

        using var mutex = new Mutex(true, demo ? "AltTabEverywhere.Demo" : "AltTabEverywhere.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.Exception);
            e.Handled = true;
        };

        using var settings = new SettingsStore();
        settings.Load();
        using var controller = new SwitcherController(settings);
        TrayIcon? tray = null;

        app.Startup += (_, _) =>
        {
            if (selfTest)
            {
                Log.Info("selftest started");
                controller.RunSelfTest(passed => app.Shutdown(passed ? 0 : 1));
                return;
            }

            if (demo)
            {
                Log.Info("demo started");
                controller.RunDemo(TimeSpan.FromSeconds(4), () => app.Shutdown());
                return;
            }

            try
            {
                controller.Start();
                tray = new TrayIcon(controller, () => app.Shutdown());
                Log.Info("started");
            }
            catch (Exception ex)
            {
                Log.Error("startup", ex);
                MessageBox.Show($"AltTab Everywhere could not start:\n\n{ex.Message}", "AltTab Everywhere",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) =>
        {
            tray?.Dispose();
            Log.Info("exited");
        };

        return app.Run();
    }
}
