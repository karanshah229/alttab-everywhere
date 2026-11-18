using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AltTabEverywhere;

/// <summary>
/// settings.json itself: where it is, reading, writing and changing keys. Shared by the switcher and
/// the settings window (which compiles this file into its own exe); the switcher's live reloading
/// is in SettingsStore.cs.
/// </summary>
internal sealed partial class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Enums as camelCase names ("allMonitors"). One generic converter per enum: no reflection, so
        // it works in the NativeAOT build too.
        Converters =
        {
            new JsonStringEnumConverter<AppTheme>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<GroupWindows>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<HighContrastMode>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<ListWindowsFrom>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<NumberKeys>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PeekMode>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<SearchMode>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<ShowOn>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<SortOrder>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<SwitcherLayout>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<VirtualDesktops>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<WindowScope>(JsonNamingPolicy.CamelCase),
        },
        // A file people edit by hand: write "Alt+Tab", not "Alt+Tab". It is never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Generated at compile time (SettingsJson below) instead of found by reflection at run time.
        TypeInfoResolver = SettingsJson.Default,
    };

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltTabEverywhere");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    /// <summary>The settings as they are in the file now (defaults if it's missing or unreadable), without watching it.</summary>
    public static Settings LoadOnce() =>
        File.Exists(FilePath) && TryRead(out var settings) ? settings : new Settings().Normalized();

    public static string Serialize(Settings settings) => JsonSerializer.Serialize(settings, Info<Settings>());

    /// <summary>
    /// Changes one key in settings.json (used by tray-menu toggles), keeping every other key as the
    /// user wrote it. Comments in the file are not preserved. The file watcher then reloads it.
    /// </summary>
    public static void SetValue(string key, System.Text.Json.Nodes.JsonNode? value) => SetValues(new[] { (key, value) });

    /// <summary>Like <see cref="SetValue"/> for several keys, written in one save (one reload).</summary>
    public static void SetValues(IEnumerable<(string Key, System.Text.Json.Nodes.JsonNode? Value)> values)
    {
        string key = "settings";
        try
        {
            System.Text.Json.Nodes.JsonObject root;
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            }
            catch (Exception ex) when (ex is JsonException or FileNotFoundException)
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }
            foreach (var (k, value) in values)
            {
                key = k;
                root[k] = value;
            }
            File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JsonOptions.Encoder }));
        }
        catch (Exception ex)
        {
            Log.Error($"save setting {key}", ex);
        }
    }

    /// <summary>A value as settings.json writes it (camelCase enums and keys), for <see cref="SetValue"/>.</summary>
    public static System.Text.Json.Nodes.JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Info<T>());

    /// <summary>The generated (de)serializer for <typeparamref name="T"/>, with settings.json's options.</summary>
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> Info<T>() =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)JsonOptions.GetTypeInfo(typeof(T));

    public static Settings Deserialize(string json) =>
        (JsonSerializer.Deserialize(json, Info<Settings>()) ?? new Settings()).Normalized();

    /// <summary>Opens settings.json in the default .json editor, or Notepad if none is associated.</summary>
    public static void OpenInEditor()
    {
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{FilePath}\"") { UseShellExecute = true });
        }
    }

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
}

/// <summary>
/// settings.json's types, serialized by code generated at compile time: no reflection, so the
/// switcher can be compiled with NativeAOT. Also every type the settings window writes on its own.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(List<Shortcut>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AppTheme))]
[JsonSerializable(typeof(GroupWindows))]
[JsonSerializable(typeof(HighContrastMode))]
[JsonSerializable(typeof(ListWindowsFrom))]
[JsonSerializable(typeof(NumberKeys))]
[JsonSerializable(typeof(PeekMode))]
[JsonSerializable(typeof(SearchMode))]
[JsonSerializable(typeof(ShowOn))]
[JsonSerializable(typeof(SortOrder))]
[JsonSerializable(typeof(SwitcherLayout))]
[JsonSerializable(typeof(VirtualDesktops))]
[JsonSerializable(typeof(WindowScope))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
internal sealed partial class SettingsJson : JsonSerializerContext;
