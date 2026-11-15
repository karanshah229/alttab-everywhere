using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AltTabEverywhere;

namespace AltTabEverywhere.Tests;

public class SettingsWindowTests
{
    /// <summary>Every setting in settings.json can be changed in the window, so none is file-only by accident.</summary>
    [Fact]
    public void The_window_edits_every_setting()
    {
        var fileKeys = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet();

        HashSet<string>? windowKeys = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new SettingsWindow(new Settings().Normalized());
                windowKeys = window.EditableKeys;
                window.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); // WPF windows need an STA thread
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.Empty(fileKeys.Except(windowKeys!));
        Assert.Empty(windowKeys!.Except(fileKeys)); // and nothing it writes is unknown to Settings
    }
}
