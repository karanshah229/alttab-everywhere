using System;
using System.Linq;
using System.Threading;

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


        // --settings (older shortcuts): the settings window is its own exe now.
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            SettingsLauncher.Launch();
            return 0;
        }

        using var mutex = new Mutex(true, demo ? "AltTabEverywhere.Demo" : "AltTabEverywhere.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance) return 0;

        // A plain Win32 message loop on this thread; work, timers and awaits all run through it.
        var ui = UiThread.Current;

        using var settings = new SettingsStore();
        settings.Load();
        using var controller = new SwitcherController(settings);
        TrayIcon? tray = null;
        SystemMessageWindow? systemEvents = null;

        ui.Post(() => // once the loop runs
        {
            if (measure)
            {
                controller.RunMeasure(sessions: 50, () => ui.Shutdown(0));
                return;
            }

            if (selfTest)
            {
                Log.Info("selftest started");
                controller.RunSelfTest(passed => ui.Shutdown(passed ? 0 : 1));
                return;
            }

            if (demo)
            {
                Log.Info("demo started");
                controller.RunDemo(DemoOptions.Parse(args), () => ui.Shutdown(0));
                return;
            }

            try
            {
                controller.Start();
                tray = new TrayIcon(controller, settings, () => ui.Shutdown(0));

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

                Log.Info($"started, version {AppInfo.Version}{(Interop.ProcessElevation.HasUiAccess ? ", with uiAccess (works over administrator windows)" : "")}");

                // checkForUpdates (opt-in): at startup, and when it's turned on; at most once a day.
                bool checkedThisRun = false;
                async void CheckForUpdates()
                {
                    if (!settings.Current.CheckForUpdates || checkedThisRun) return;
                    checkedThisRun = true;
                    var known = UpdateCheck.Known();
                    if (known is not null) tray?.ShowUpdate(known, notify: false);
                    if (!UpdateCheck.Due(DateTime.UtcNow)) return;
                    var found = await UpdateCheck.CheckAsync();
                    if (found is not null && found.Version != known?.Version) tray?.ShowUpdate(found, notify: true);
                    controller.TrimMemorySoon(); // give back what the HTTPS request loaded
                }
                settings.Changed += CheckForUpdates;
                ui.PostIdle(CheckForUpdates);
                // Warm up the switcher (invisibly) so the first Alt+Tab is fast, then give back
                // the memory that startup and warm-up touched.
                ui.PostIdle(controller.WarmUpThenTrim);
            }
            catch (Exception ex)
            {
                Log.Error("startup", ex);
                ShowError($"AltTab Everywhere could not start:\n\n{ex.Message}");
                ui.Shutdown(1);
            }
        });

        int exitCode = UiThread.Run();
        systemEvents?.Dispose();
        tray?.Dispose();
        Log.Info("exited");
        return exitCode;
    }

    private static void ShowError(string message)
    {
        unsafe
        {
            fixed (char* text = message)
            fixed (char* title = "AltTab Everywhere")
                MessageBoxW(IntPtr.Zero, text, title, 0x10 /* MB_ICONERROR */);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern unsafe int MessageBoxW(IntPtr owner, char* text, char* caption, uint type);
}
