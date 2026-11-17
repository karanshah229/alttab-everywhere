using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// The Win32 switcher as screen readers (UI Automation) see it, as the WPF one is seen: the window,
/// holding a list named "Windows" with one item per tile (named after its window, with its place in
/// the list), and "No windows match" as a live region when a search finds nothing. The selected tile
/// has the focus, so screen readers read each tile as it's selected.
///
/// Nothing here exists until a screen reader asks (WM_GETOBJECT), and events are only raised while
/// one is listening, so this costs nothing otherwise. Calls arrive on the window's own thread.
/// </summary>
internal sealed class SwitcherAutomation
{
    /// <summary>What the providers need from the window, in screen pixels.</summary>
    internal interface IHost
    {
        IntPtr Handle { get; }
        int TileCount { get; }
        string TileName(int index);
        int Selected { get; }
        bool Announce { get; }
        bool EmptyShown { get; }
        RECT TileScreenRect(int index);
        bool TileOffscreen(int index);
        RECT ListScreenRect { get; }
        RECT EmptyScreenRect { get; }
        int TileAtScreen(int x, int y);
    }

    private readonly IHost _host;
    private SwitcherAutomationNode? _root, _list, _empty;
    private readonly List<SwitcherAutomationNode?> _tiles = new();
    private readonly List<SwitcherAutomationNode> _handedOut = new(); // nodes a client may hold: disconnected when stale
    private bool _rootHandedOut;
    private static int _nextId;

    public SwitcherAutomation(IHost host) => _host = host;

    internal IHost Host => _host;

    internal SwitcherAutomationNode Root => _root ??= new SwitcherAutomationNode(this, SwitcherAutomationNode.Kind.Root, 0, 0);
    internal SwitcherAutomationNode List => _list ??= Node(SwitcherAutomationNode.Kind.List, 0);
    internal SwitcherAutomationNode Empty => _empty ??= Node(SwitcherAutomationNode.Kind.Empty, 0);

    internal SwitcherAutomationNode? Tile(int index)
    {
        if (index < 0 || index >= _host.TileCount) return null;
        while (_tiles.Count <= index) _tiles.Add(null);
        return _tiles[index] ??= Node(SwitcherAutomationNode.Kind.Tile, index);
    }

    private SwitcherAutomationNode Node(SwitcherAutomationNode.Kind kind, int index) => new(this, kind, index, ++_nextId);

    /// <summary>A pointer for a client: from now on the node may be held, so it's disconnected when it goes stale.</summary>
    internal nint HandOut<T>(SwitcherAutomationNode? node) where T : class
    {
        if (node is null) return 0;
        if (node.NodeKind != SwitcherAutomationNode.Kind.Root && !_handedOut.Contains(node)) _handedOut.Add(node);
        return Uia.Pointer(node as T);
    }

    /// <summary>WM_GETOBJECT: hands UI Automation the window's root.</summary>
    public nint OnGetObject(nint wParam, nint lParam)
    {
        nint root = Uia.Pointer<IRawElementProviderSimple>(Root);
        try
        {
            _rootHandedOut = true;
            return Uia.UiaReturnRawElementProvider(_host.Handle, wParam, lParam, root);
        }
        finally
        {
            Marshal.Release(root);
        }
    }

    /// <summary>A new session's tiles: the old tiles (and list) are gone for clients.</summary>
    public void SessionChanged(bool wasEmpty)
    {
        DisconnectStale();
        if (_host.Announce && _host.EmptyShown && !wasEmpty) Raise(Empty, Uia.LiveRegionChangedEvent);
    }

    /// <summary>The selection moved: screen readers read the newly focused tile (only the active switcher raises it).</summary>
    public void SelectionChanged()
    {
        if (!_host.Announce || Native.GetForegroundWindow() != _host.Handle) return;
        if (Tile(_host.Selected) is { } tile) Raise(tile, Uia.FocusChangedEvent);
    }

    private static void Raise(SwitcherAutomationNode node, int eventId)
    {
        if (Uia.UiaClientsAreListening() == 0) return;
        nint pointer = Uia.Pointer<IRawElementProviderSimple>(node);
        try { Uia.UiaRaiseAutomationEvent(pointer, eventId); }
        finally { Marshal.Release(pointer); }
    }

    private void DisconnectStale()
    {
        foreach (var node in _handedOut)
        {
            node.Stale = true;
            nint pointer = Uia.Pointer<IRawElementProviderSimple>(node);
            Uia.UiaDisconnectProvider(pointer);
            Marshal.Release(pointer);
        }
        _handedOut.Clear();
        _tiles.Clear();
        _list = null;
        _empty = null;
    }

    /// <summary>The window is being destroyed.</summary>
    public void Release()
    {
        DisconnectStale();
        if (_rootHandedOut)
        {
            Uia.UiaReturnRawElementProvider(_host.Handle, 0, 0, 0);
            if (_root is not null)
            {
                _root.Stale = true;
                nint pointer = Uia.Pointer<IRawElementProviderSimple>(_root);
                Uia.UiaDisconnectProvider(pointer);
                Marshal.Release(pointer);
            }
        }
        _root = null;
    }

    /// <summary>The children of the window: the "No windows match" text (when shown), then the list.</summary>
    internal List<SwitcherAutomationNode> RootChildren()
    {
        var children = new List<SwitcherAutomationNode>(2);
        if (_host.EmptyShown) children.Add(Empty);
        children.Add(List);
        return children;
    }
}

/// <summary>One element the screen reader sees: the window, the tile list, a tile or the empty-search text.</summary>
[GeneratedComClass]
internal sealed partial class SwitcherAutomationNode : IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot
{
    internal enum Kind { Root, List, Tile, Empty }

    private readonly SwitcherAutomation _owner;
    private readonly int _id;
    internal Kind NodeKind { get; }
    internal int Index { get; }
    /// <summary>Its session ended: every call fails with "element not available".</summary>
    internal bool Stale { get; set; }

    internal SwitcherAutomationNode(SwitcherAutomation owner, Kind kind, int index, int id)
    {
        _owner = owner;
        NodeKind = kind;
        Index = index;
        _id = id;
    }

    private SwitcherAutomation.IHost Host => _owner.Host;

    private void EnsureAlive()
    {
        if (Stale || (NodeKind == Kind.Tile && Index >= Host.TileCount)) throw new COMException("Element not available", Uia.ElementNotAvailable);
    }

    // ---- IRawElementProviderSimple ----

    public int get_ProviderOptions() => Uia.ProviderOptionsServerSide;

    public nint GetPatternProvider(int patternId) => 0;

    public UiaVariant GetPropertyValue(int propertyId)
    {
        EnsureAlive();
        switch (NodeKind)
        {
            case Kind.Root:
                // A frameless popup would read as a pane: say what it is, as the WPF switcher does.
                return propertyId switch
                {
                    Uia.ControlTypeProperty => UiaVariant.Of(Uia.WindowControlType),
                    Uia.NameProperty => UiaVariant.Of("AltTab Everywhere"),
                    _ => default, // the rest from the window's own provider (the HWND)
                };
            case Kind.List:
                return propertyId switch
                {
                    Uia.ControlTypeProperty => UiaVariant.Of(Uia.ListControlType),
                    Uia.NameProperty => UiaVariant.Of("Windows"),
                    Uia.ClassNameProperty => UiaVariant.Of("AltTabEverywhere.TileList"),
                    Uia.IsControlElementProperty or Uia.IsContentElementProperty or Uia.IsEnabledProperty => UiaVariant.Of(true),
                    _ => default,
                };
            case Kind.Empty:
                return propertyId switch
                {
                    Uia.ControlTypeProperty => UiaVariant.Of(Uia.TextControlType),
                    Uia.NameProperty => UiaVariant.Of("No windows match"),
                    Uia.LiveSettingProperty => UiaVariant.Of(Uia.LiveAssertive),
                    Uia.IsControlElementProperty or Uia.IsContentElementProperty or Uia.IsEnabledProperty => UiaVariant.Of(true),
                    _ => default,
                };
            default: // a tile
                return propertyId switch
                {
                    Uia.ControlTypeProperty => UiaVariant.Of(Uia.ListItemControlType),
                    Uia.NameProperty => UiaVariant.Of(Host.TileName(Index)),
                    Uia.ClassNameProperty => UiaVariant.Of("AltTabEverywhere.Tile"),
                    Uia.PositionInSetProperty => UiaVariant.Of(Index + 1),
                    Uia.SizeOfSetProperty => UiaVariant.Of(Host.TileCount),
                    Uia.IsKeyboardFocusableProperty => UiaVariant.Of(Host.Announce),
                    // Focus within its own switcher, as WPF's logical focus: every monitor's switcher has one.
                    Uia.HasKeyboardFocusProperty => UiaVariant.Of(Host.Announce && Host.Selected == Index),
                    Uia.IsOffscreenProperty => UiaVariant.Of(Host.TileOffscreen(Index)),
                    Uia.IsControlElementProperty or Uia.IsContentElementProperty or Uia.IsEnabledProperty => UiaVariant.Of(true),
                    _ => default,
                };
        }
    }

    public nint get_HostRawElementProvider()
    {
        if (NodeKind != Kind.Root || Stale) return 0;
        unsafe
        {
            nint provider;
            return Uia.UiaHostProviderFromHwnd(Host.Handle, &provider) == 0 ? provider : 0;
        }
    }

    // ---- IRawElementProviderFragment ----

    public nint Navigate(int direction)
    {
        EnsureAlive();
        SwitcherAutomationNode? target = null;
        switch (NodeKind)
        {
            case Kind.Root:
                var children = _owner.RootChildren();
                if (direction == Uia.NavigateFirstChild) target = children[0];
                else if (direction == Uia.NavigateLastChild) target = children[^1];
                break;
            case Kind.List:
            case Kind.Empty:
                var siblings = _owner.RootChildren();
                int at = siblings.IndexOf(this);
                target = direction switch
                {
                    Uia.NavigateParent => _owner.Root,
                    Uia.NavigateNextSibling when at >= 0 && at + 1 < siblings.Count => siblings[at + 1],
                    Uia.NavigatePreviousSibling when at > 0 => siblings[at - 1],
                    Uia.NavigateFirstChild when NodeKind == Kind.List => _owner.Tile(0),
                    Uia.NavigateLastChild when NodeKind == Kind.List => _owner.Tile(Host.TileCount - 1),
                    _ => null,
                };
                break;
            case Kind.Tile:
                target = direction switch
                {
                    Uia.NavigateParent => _owner.List,
                    Uia.NavigateNextSibling => _owner.Tile(Index + 1),
                    Uia.NavigatePreviousSibling => _owner.Tile(Index - 1),
                    _ => null,
                };
                break;
        }
        return target is null ? 0
            : ReferenceEquals(target, _owner.Root) ? Uia.Pointer<IRawElementProviderFragment>(target)
            : _owner.HandOut<IRawElementProviderFragment>(target);
    }

    public nint GetRuntimeId()
    {
        EnsureAlive();
        return NodeKind == Kind.Root ? 0 : Uia.IntArray(Uia.AppendRuntimeId, _id); // the root's comes from its window
    }

    public UiaRect get_BoundingRectangle()
    {
        EnsureAlive();
        RECT r = NodeKind switch
        {
            Kind.List => Host.ListScreenRect,
            Kind.Empty => Host.EmptyScreenRect,
            Kind.Tile => Host.TileScreenRect(Index),
            _ => default, // the window's own provider gives the root's
        };
        return new UiaRect { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height };
    }

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() { } // the keyboard drives the selection, not the screen reader

    public nint get_FragmentRoot() => Uia.Pointer<IRawElementProviderFragmentRoot>(_owner.Root);

    // ---- IRawElementProviderFragmentRoot (the window) ----

    public nint ElementProviderFromPoint(double x, double y)
    {
        EnsureAlive();
        int index = Host.TileAtScreen((int)x, (int)y);
        if (index >= 0) return _owner.HandOut<IRawElementProviderFragment>(_owner.Tile(index));
        var list = Host.ListScreenRect;
        return x >= list.Left && x < list.Right && y >= list.Top && y < list.Bottom ? _owner.HandOut<IRawElementProviderFragment>(_owner.List) : 0;
    }

    public nint GetFocus()
    {
        EnsureAlive();
        return Host.Announce ? _owner.HandOut<IRawElementProviderFragment>(_owner.Tile(Host.Selected)) : 0;
    }
}
