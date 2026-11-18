using System;
using System.IO;

namespace AltTabEverywhere;

/// <summary>
/// Loads settings.json, creates it with defaults on first run, and reloads it whenever it is saved.
/// A file that fails to parse is logged and ignored, so a typo never breaks the running switcher.
/// (Reading and writing the file itself is in SettingsFile.cs.)
/// </summary>
internal sealed partial class SettingsStore : IDisposable
{
    private readonly UiThread _thread;
    private readonly UiTimer _debounce;
    private FileSystemWatcher? _watcher;

    public Settings Current { get; private set; } = new Settings().Normalized();

    /// <summary>Raised on the UI thread after a successful reload.</summary>
    public event Action? Changed;

    public SettingsStore()
    {
        _thread = UiThread.Current;
        // Editors often write a file in several steps; wait for them to finish.
        _debounce = new UiTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (TryRead(out var settings))
            {
                Current = settings;
                Log.Info("settings reloaded");
                Changed?.Invoke();
            }
        };
    }

    public void Load()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!File.Exists(FilePath)) WriteDefaults();
            if (TryRead(out var settings)) Current = settings;
            StartWatching();
        }
        catch (Exception ex)
        {
            Log.Error("load settings", ex);
        }
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(Directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _thread.Post(RestartDebounce);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => _thread.Post(RestartDebounce);
        _watcher.EnableRaisingEvents = true;
    }

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _debounce.Stop();
        _watcher?.Dispose();
    }
}
