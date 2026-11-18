using System.Collections.Generic;
using System.Linq;

namespace AltTabEverywhere;

/// <summary>What screen readers hear for a tile (see <see cref="SwitcherAutomation"/>).</summary>
internal static class TileAutomation
{
    /// <summary>
    /// What a screen reader says for a tile: its title, then how many windows a stack holds and any
    /// states (not responding, administrator...). Position ("3 of 12") is announced separately.
    /// </summary>
    public static string NameOf(SwitcherItem item, IReadOnlyList<StateBadge> states)
    {
        if (item.IsBack) return "Back to all windows";
        if (item.IsDesktop) return SwitcherItem.DesktopTitle;
        var parts = new List<string> { string.IsNullOrWhiteSpace(item.Shown.Title) ? "Untitled window" : item.Shown.Title };
        if (item.IsStack) parts.Add($"{item.Count} windows");
        parts.AddRange(states.Select(s => s.Label.ToLowerInvariant()));
        return string.Join(", ", parts);
    }
}
