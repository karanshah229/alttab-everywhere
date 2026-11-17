using System.Reflection;

namespace AltTabEverywhere;

internal static class AppInfo
{
    /// <summary>"0.6.0": the project's Version, without the build metadata .NET appends.</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
