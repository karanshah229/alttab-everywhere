using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Threading;

namespace AltTabEverywhere;

/// <summary>
/// Loads settings.json, creates it with defaults on first run, and reloads it whenever it is saved.
/// A file that fails to parse is logged and ignored, so a typo never breaks the running switcher.
/// </summary>
internal sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _watcher;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltTabEverywhere");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public Settings Current { get; private set; } = new Settings().Normalized();

    /// <summary>Raised on the UI thread after a successful reload.</summary>
    public event Action? Changed;

    public SettingsStore()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Editors often write a file in several steps; wait for them to finish.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
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

    public static string Serialize(Settings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    public static Settings Deserialize(string json) =>
        (JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings()).Normalized();

    private static void WriteDefaults()
    {
        File.WriteAllText(FilePath, Serialize(new Settings()));
        Log.Info("wrote default settings file");
    }

    private static bool TryRead(out Settings settings)
    {
        settings = new Settings();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                settings = Deserialize(File.ReadAllText(FilePath));
                return true;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50); // the editor may still hold the file
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Info($"settings file ignored: {ex.Message}");
                return false;
            }
        }
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(Directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _dispatcher.BeginInvoke(RestartDebounce);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => _dispatcher.BeginInvoke(RestartDebounce);
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
