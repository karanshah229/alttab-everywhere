using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AltTabEverywhere;

/// <summary>A newer release: its version and its page on GitHub.</summary>
internal sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// checkForUpdates (opt-in): asks GitHub for the latest published release, at most once a day and
/// only at startup (or when the setting is turned on). One HTTPS request, no timers, nothing about
/// the user is sent: the request carries only the app's name and version.
/// </summary>
internal static class UpdateCheck
{
    public const string ReleasesPrefix = "https://github.com/karanshah229/alttab-everywhere/releases/";
    private const string LatestApi = "https://api.github.com/repos/karanshah229/alttab-everywhere/releases/latest";
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>When the last check ran and what it found ("" when nothing newer), kept between runs.</summary>
    private static string StatePath => Path.Combine(Log.Directory, "update-check.txt");

    /// <summary>A newer version found by an earlier check, still newer than this one; or null.</summary>
    public static UpdateInfo? Known()
    {
        var (_, found) = ReadState();
        return found is not null && IsNewer(found.Version, AppInfo.Version) ? found : null;
    }

    /// <summary>Whether a day has passed since the last check (or there never was one).</summary>
    public static bool Due(DateTime nowUtc) => ReadState().LastUtc is not { } last || nowUtc - last >= Interval || last > nowUtc;

    /// <summary>Asks GitHub. Returns the newer release, or null (none newer, no releases yet, or no connection).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AltTabEverywhere/{AppInfo.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(LatestApi).ConfigureAwait(true);
            UpdateInfo? latest = null;
            if (response.StatusCode != HttpStatusCode.NotFound) // 404: no release published yet
            {
                response.EnsureSuccessStatusCode();
                latest = Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            }
            var newer = latest is not null && IsNewer(latest.Version, AppInfo.Version) ? latest : null;
            WriteState(DateTime.UtcNow, newer);
            Log.Info(newer is null ? "update check: up to date" : $"update check: version {newer.Version} is available");
            return newer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Log.Info($"update check: failed ({ex.GetType().Name}); will try again at a later startup");
            return null;
        }
    }

    /// <summary>The release in GitHub's "latest release" JSON, or null if it's unusable or points anywhere but this project's releases.</summary>
    internal static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } tagName) return null;
        if (!root.TryGetProperty("html_url", out var url) || url.GetString() is not { } page) return null;
        // Only ever open this project's own release pages.
        if (!page.StartsWith(ReleasesPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        string version = tagName.TrimStart('v', 'V');
        return TryVersion(version, out _) ? new UpdateInfo(version, page) : null;
    }

    /// <summary>Whether <paramref name="candidate"/> ("0.7.0") is a later version than <paramref name="current"/>.</summary>
    internal static bool IsNewer(string candidate, string current) =>
        TryVersion(candidate, out var a) && TryVersion(current, out var b) && a > b;

    private static bool TryVersion(string text, out Version version)
    {
        // "0.7.0-beta" or "0.7.0+build": compare the numbers only.
        string numbers = text.Split('-', '+')[0];
        if (Version.TryParse(numbers, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
        version = new Version();
        return false;
    }

    private static (DateTime? LastUtc, UpdateInfo? Found) ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return (null, null);
            var lines = File.ReadAllLines(StatePath);
            DateTime? last = lines.Length > 0 && DateTime.TryParse(lines[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            UpdateInfo? found = lines.Length > 2 && lines[1].Length > 0 && lines[2].StartsWith(ReleasesPrefix, StringComparison.OrdinalIgnoreCase)
                ? new UpdateInfo(lines[1], lines[2]) : null;
            return (last, found);
        }
        catch (IOException)
        {
            return (null, null);
        }
    }

    private static void WriteState(DateTime nowUtc, UpdateInfo? found)
    {
        Directory.CreateDirectory(Log.Directory);
        File.WriteAllLines(StatePath, new[]
        {
            nowUtc.ToString("o", CultureInfo.InvariantCulture),
            found?.Version ?? "",
            found?.Url ?? "",
        });
    }
}
