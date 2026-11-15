using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AltTabEverywhere;

/// <summary>
/// A tile's outer border, seen by screen readers (UI Automation) as a list item named after its
/// window. Peers are only created when an automation client asks, so this costs nothing otherwise.
/// </summary>
internal sealed class TileBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new TilePeer(this);

    private sealed class TilePeer(TileBorder owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "AltTabEverywhere.Tile";
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        // Its title, icon and badges are summed up in its name; reading them again would be noise.
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>The tiles' panel, seen by screen readers as a list.</summary>
internal sealed class TilePanel : WrapPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new TilePanelPeer(this);

    private sealed class TilePanelPeer(TilePanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => "AltTabEverywhere.TileList";
    }
}

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

    public static void Describe(FrameworkElement tile, string name, int position, int count)
    {
        AutomationProperties.SetName(tile, name);
        AutomationProperties.SetPositionInSet(tile, position);
        AutomationProperties.SetSizeOfSet(tile, count);
    }
}
