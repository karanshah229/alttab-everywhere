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
        bool measure = args.Contains("--measure", StringComparer.OrdinalIgnoreCase);
        demo |= selfTest | measure; // same single-instance rules: never touches the keyboard hook

        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            // Writes window titles, so it goes to its own file that only this flag creates.
            string path = System.IO.Path.Combine(Log.Directory, "windows.txt");
            System.IO.Directory.CreateDirectory(Log.Directory);
            System.IO.File.WriteAllLines(path, WindowEnumerator.DescribeSources());
            return 0;
        }

        // The overlay is a few rectangles and text; live previews are composited by DWM, not WPF.
        // Software rendering avoids initialising a Direct3D device and the GPU driver, which
        // costs ~115 MB private memory and ~1,200 handles for the life of the process.
        // (--hardware-rendering exists only to compare the two with --measure.)
        if (!args.Contains("--hardware-rendering", StringComparer.OrdinalIgnoreCase))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // The settings window runs in its own process, which exits when the window closes, so
        // the always-on switcher never holds its memory. The switcher picks up each change from
        // settings.json.
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            return RunSettingsWindow();

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
        SystemMessageWindow? systemEvents = null;

        app.Startup += (_, _) =>
        {
            if (measure)
            {
                controller.RunMeasure(sessions: 50, () => app.Shutdown());
                return;
            }

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
                tray = new TrayIcon(controller, settings, () => app.Shutdown());

                // Start with Windows: applied now and whenever settings.json changes.
                StartupRegistration.Apply(settings.Current.StartWithWindows);
                settings.Changed += () => StartupRegistration.Apply(settings.Current.StartWithWindows);

                // System notifications instead of polling: re-install the hook after sleep or
                // unlock, rebuild overlays after monitor changes.
                systemEvents = new SystemMessageWindow();
                systemEvents.HookMayBeLost += controller.ReinstallHook;
                systemEvents.DisplayChanged += controller.OnDisplayChanged;
                systemEvents.ColorsChanged += controller.OnSystemColorsChanged;
                systemEvents.ExplorerRestarted += () => Log.Info("Explorer restarted");

                Log.Info($"started, version {AppInfo.Version}");
                // Warm up the switcher (invisibly) so the first Alt+Tab is fast, then give back
                // the memory that startup and warm-up touched.
                app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(controller.WarmUpThenTrim));
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
            systemEvents?.Dispose();
            tray?.Dispose();
            Log.Info("exited");
        };

        return app.Run();
    }

    private static int RunSettingsWindow()
    {
        using var mutex = new Mutex(true, "AltTabEverywhere.Settings", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SettingsWindow.ActivateExisting(); // already open: bring it to the front
            return 0;
        }

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
