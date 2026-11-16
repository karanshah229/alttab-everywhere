using System;
using System.Globalization;

namespace AltTabEverywhere;

/// <summary>
/// --demo's options, for testing and for making the README's screenshots:
/// <c>--demo-seconds N</c> (how long it stays open, default 4), <c>--demo-defaults</c> (ignore
/// settings.json and use the defaults), <c>--demo-since TIME</c> (only windows of processes started
/// at or after TIME, an ISO 8601 UTC time: shows staged sample windows and nothing else), and
/// <c>--demo-tour MS</c> (move the selection one tile every MS milliseconds).
/// </summary>
internal sealed record DemoOptions(TimeSpan Duration, bool Defaults, DateTime? Since, int TourMs)
{
    public static DemoOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        double seconds = double.TryParse(Value("--demo-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 120) : 4;
        DateTime? since = DateTime.TryParse(Value("--demo-since"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        int tour = int.TryParse(Value("--demo-tour"), out var ms) ? Math.Clamp(ms, 100, 10000) : 0;
        bool defaults = Array.Exists(args, a => string.Equals(a, "--demo-defaults", StringComparison.OrdinalIgnoreCase));
        return new DemoOptions(TimeSpan.FromSeconds(seconds), defaults, since, tour);
    }
}
