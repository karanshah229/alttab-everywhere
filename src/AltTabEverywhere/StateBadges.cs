using System.Collections.Generic;

namespace AltTabEverywhere;

/// <summary>A small symbol on a tile (Segoe Fluent Icons glyph) with the text its tooltip shows.</summary>
internal sealed record StateBadge(string Glyph, string Label);

/// <summary>
/// The state badges a window's tile shows: things that explain a surprise, such as a window that
/// won't come forward (not responding) or won't take keys from this app (running as administrator).
/// </summary>
internal static class StateBadges
{
    public static readonly StateBadge NotResponding = new("", "Not responding");
    public static readonly StateBadge Admin = new("", "Running as administrator");
    public static readonly StateBadge OtherDesktop = new("", "On another desktop");
    public static readonly StateBadge AlwaysOnTop = new("", "Always on top");
    public static readonly StateBadge Minimized = new("", "Minimized");

    /// <summary>The badges that are on in <paramref name="settings"/> and apply to the window, most important first.</summary>
    public static List<StateBadge> For(WindowInfo window, Settings settings)
    {
        var badges = new List<StateBadge>(0);
        if (settings.NotRespondingBadge && window.IsHung) badges.Add(NotResponding);
        if (settings.AdminBadge && window.IsElevated) badges.Add(Admin);
        if (settings.AlwaysOnTopBadge && window.IsTopmost) badges.Add(AlwaysOnTop);
        if (settings.OtherDesktopBadge && window.OnOtherDesktop) badges.Add(OtherDesktop);
        if (settings.MinimizedBadge && window.IsMinimized) badges.Add(Minimized);
        return badges;
    }
}
