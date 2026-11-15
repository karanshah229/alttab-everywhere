using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// Owns one Alt+Tab session: which windows are listed, how they're split across monitors, the
/// selection, and the per-monitor overlays. Runs on the main UI thread (which also services the
/// keyboard hook); each overlay runs on its own thread via <see cref="OverlayHost"/>.
///
/// The keyboard always drives the "focus" overlay (the monitor under the mouse, or the one with the
/// active window). When every monitor lists the same windows, all overlays mirror its selection;
/// when each monitor lists only its own windows, the others can still be clicked.
/// </summary>
internal sealed partial class SwitcherController : IDisposable
{
    private readonly SettingsStore _store;
    private readonly KeyboardHook _hook = new();
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _stackTimer; // resting on a stack opens it; on Back, goes back
    private readonly DispatcherTimer _displayChangeTimer;
    private readonly MemoryTrimmer _trimmer = new(TimeSpan.FromSeconds(2));
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<OverlayHost> _overlays = new();
    private string _monitorLayoutKey = "";

    // ---- Session state ----
    private List<WindowInfo> _windows = new();   // every window in this session, after exclusions
    private List<PlanEntry> _plan = new();       // which overlays show which tiles
    private List<SwitcherItem> _items = new();   // the focus overlay's tiles (what the keyboard drives)
    private int _selected;
    private int _columns = 1;                    // the focus overlay's columns (for Up/Down)
    private OverlayHost? _focusOverlay;
    private IntPtr _originalForeground;
    private bool _active;
    private int _sessionId;             // bumped on every begin/end, to drop stale async work
    private bool _preparing;            // overlays are being built (cloaked)
    private bool _uiPrepared;           // overlays built and drawn (cloaked until revealed)
    private bool _uiShown;              // overlays visible
    private bool _revealRequested;      // show as soon as the build finishes
    private bool _warmingUp;
    private Settings? _sessionSettings; // the global settings with the session's shortcut's overrides
    private List<string> _searchStrokes = new(); // what each search keystroke typed (Backspace removes one)
    private string _query = "";
    private string _plannedQuery = "";  // the query the current plan was built for
    private Dictionary<IntPtr, string>? _searchText; // per window, built on the first keystroke of a session
    private SwitcherItem? _openStack;   // the stack whose windows the focus switcher shows, with a Back tile
    private int _stackReturnIndex;      // where that stack's tile was, to select it again on Back
    private bool _stackRequested;       // the key above Tab before the tiles were built: open once they are
    private int _stackDelayMs;          // stackOpenDelayMs (0 = only the key and the badge open a stack)

    private List<(Shortcut Shortcut, ShortcutKeys Keys)> _shortcuts = new();

    private sealed record PlanEntry(OverlayHost Host, List<SwitcherItem> Items, int Columns, bool MirrorsSelection);

    public SwitcherController(SettingsStore store)
    {
        _store = store;
        _showTimer = new DispatcherTimer();
        _showTimer.Tick += (_, _) => RevealUi();
        _stackTimer = new DispatcherTimer();
        _stackTimer.Tick += (_, _) => OnStackTimer();
        _displayChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            if (!_active) WarmUpThenTrim(); // rebuild overlays for the new layout before the next Alt+Tab
        };

        _hook.Shortcut += OnShortcut;
        _hook.Move += Move;
        _hook.MoveRow += MoveRow;
        _hook.Jump += Jump;
        _hook.StackKey += OnStackKey;
        _hook.StackBack += CloseStack;
        _hook.CloseSelected += CloseSelected;
        _hook.ModifiersReleased += OnModifiersReleased;
        _hook.Commit += Commit;
        _hook.Cancel += Cancel;
        _hook.Number += OnNumber;
        _hook.SearchType += text => SetQuery(_searchStrokes.Append(text).ToList());
        _hook.SearchBackspace += () => SetQuery(_searchStrokes.Take(Math.Max(0, _searchStrokes.Count - 1)).ToList());
        _hook.SearchClear += () => SetQuery(new List<string>());

        _store.Changed += ApplySettings;
        ApplySettings();
    }

    private Settings Settings => _settingsOverride ?? _sessionSettings ?? _store.Current;

    /// <summary>Only set by --selftest, to exercise other settings without touching the user's file.</summary>
    private Settings? _settingsOverride;

    public bool Enabled
    {
        get => _hook.Enabled;
        set
        {
            _hook.Enabled = value;
            if (!value) End();
            else ReinstallHook("re-enabled from the tray");
        }
    }

    public void Start() => _hook.Install();

    private void ApplySettings()
    {
        var settings = _store.Current;
        // The shortcut list may change under an open session; close it rather than mix the two.
        if (_active && !_warmingUp) End();
        _shortcuts = settings.ValidShortcuts(out var rejected);
        foreach (string entry in rejected) Log.Info($"shortcut {entry} ignored");
        _hook.SetBindings(_shortcuts
            .Select(s => new KeyboardHook.Binding(s.Keys, settings.ForShortcut(s.Shortcut).StayOpenAfterAltRelease))
            .ToList());
        Log.Info("shortcuts: " + string.Join(", ", _shortcuts.Select(s => s.Keys.ToString())));
        _hook.DeleteCloses = settings.DeleteKeyCloses;
        _hook.TypeToSearch = settings.TypeToSearch;
        _hook.NumberKeys = settings.NumberKeys != NumberKeys.Off;
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, settings.ShowDelayMs));
        _stackDelayMs = settings.StackOpenDelayMs;
        Theme.Rebuild(settings);
    }

    /// <summary>Windows' light/dark mode or accent colour changed: the next session picks it up.</summary>
    public void OnSystemColorsChanged()
    {
        var theme = Theme.Rebuild(_store.Current);
        Log.Info($"theme: {(theme.IsDark ? "dark" : "light")}, accent {theme.AccentColor}");
    }

    /// <summary>The settings for a session opened by shortcut <paramref name="index"/>.</summary>
    private Settings SettingsFor(int index) =>
        index >= 0 && index < _shortcuts.Count ? _store.Current.ForShortcut(_shortcuts[index].Shortcut) : _store.Current;

    // ---- System events (no polling) ----

    /// <summary>Windows may have dropped the low-level hook (sleep, unlock...): re-install it.</summary>
    public void ReinstallHook(string reason)
    {
        try
        {
            End();
            _hook.Reinstall();
            Log.Info($"keyboard hook re-installed ({reason})");
        }
        catch (Exception ex)
        {
            Log.Error("re-install hook", ex);
        }
    }

    /// <summary>Monitors changed: close any session and rebuild the overlays shortly after.</summary>
    public void OnDisplayChanged()
    {
        End();
        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    /// <summary>
    /// Builds the switcher once, cloaked, so the next real Alt+Tab doesn't pay for creating the
    /// overlay threads and compiling the code; then trims memory. Nothing is shown.
    /// </summary>
    public async void WarmUpThenTrim()
    {
        try
        {
            _sessionSettings = SettingsFor(0);
            if (!_active && LoadWindows() > 0)
            {
                _warmingUp = true;
                _active = true;
                _sessionId++;
                EnsureOverlays();
                PlanSession(keepFocus: null);
                _selected = 0;
                PrepareUi();
                for (int waited = 0; !_uiPrepared && _warmingUp && waited < 3000; waited += 10) await Task.Delay(10);
                await Task.Delay(200);
                if (_warmingUp) End(); // unless a real Alt+Tab has taken over meanwhile
            }
        }
        catch (Exception ex)
        {
            Log.Error("warm up", ex);
        }
        finally
        {
            _warmingUp = false;
            if (!_active) _sessionSettings = null;
        }
        // Trim on the usual schedule (2 s later): trimming right away runs while WPF is still
        // tearing down the hidden frame, and the pages come straight back.
        if (!_active) _trimmer.Schedule();
    }

    /// <summary>--demo: open the switcher without the keyboard, log the overlay layout, then close.</summary>
    public async void RunDemo(TimeSpan duration, Action done)
    {
        BeginSession(reverse: false, sendMaskKey: false);
        await WaitUntilShown();
        await Task.Delay(700);
        Log.Info($"demo: windows={_windows.Count} focus tiles={_items.Count} stacks={_items.Count(i => i.IsStack)} " +
                 $"selected={_selected} overlays shown={_plan.Count}/{_overlays.Count} columns={_columns}");
        foreach (var entry in _plan) Log.Info("demo: " + entry.Host.Query(w => w.DescribeForLog()));
        await Task.Delay(duration);
        Cancel();
        done();
    }

    // ---- Session ----

    private void OnShortcut(int shortcut, bool isFirst, bool reverse)
    {
        if (!isFirst && _active)
        {
            Move(reverse ? -1 : +1);
            return;
        }
        BeginSession(reverse, sendMaskKey: true, shortcut);
    }

    /// <summary>Lists the session's windows (with exclusions and the shortcut's scope). Returns how many.</summary>
    private int LoadWindows()
    {
        var settings = Settings;
        var windows = WindowEnumerator.GetSwitchableWindows(settings.VirtualDesktops == VirtualDesktops.All);
        _windows = WindowFilter.ApplyExclusions(windows, settings, WindowEnumerator.ProcessPathOf);
        if (settings.SessionWindows == WindowScope.CurrentApp) _windows = CurrentAppWindows(_windows);
        return _windows.Count;
    }

    /// <summary>The windows of the app that had focus; none if focus wasn't on a listed window.</summary>
    private List<WindowInfo> CurrentAppWindows(List<WindowInfo> windows)
    {
        var focused = windows.FirstOrDefault(w => w.Handle == _originalForeground);
        if (focused is null)
        {
            // Focus may be on a dialog or other owned window: use the window that owns it.
            IntPtr owner = Native.GetAncestor(_originalForeground, Native.GA_ROOTOWNER);
            focused = windows.FirstOrDefault(w => w.Handle == owner);
        }
        if (focused is null) return new List<WindowInfo>();
        string app = WindowEnumerator.AppKey(focused);
        return windows.Where(w => string.Equals(WindowEnumerator.AppKey(w), app, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void BeginSession(bool reverse, bool sendMaskKey, int shortcut = 0)
    {
        try
        {
            if (_warmingUp)
            {
                _warmingUp = false; // a real Alt+Tab arrived during warm-up: drop the warm-up session
                End();
            }
            _sessionSettings = SettingsFor(shortcut);
            _searchStrokes = new List<string>();
            _query = _plannedQuery = "";
            _openStack = null;
            _stackRequested = false;
            _trimmer.Cancel();
            if (sendMaskKey) WindowActivator.SendMenuMaskKey();
            _timing?.Mark("begin");
            _originalForeground = Native.GetForegroundWindow();
            if (LoadWindows() == 0)
            {
                _hook.EndSession();
                _sessionSettings = null;
                return;
            }
            _timing?.Mark("windows listed");

            _active = true;
            _sessionId++;
            _revealRequested = false;
            EnsureOverlays();
            PlanSession(keepFocus: null);
            if (_items.Count == 0)
            {
                End();
                return;
            }

            // Like Windows: start on the previous tile, unless focus isn't on a listed window.
            int forwardStart = _items.Count > 1 && _items[0].Contains(_originalForeground) ? 1 : 0;
            _selected = reverse ? _items.Count - 1 : forwardStart;

            // Build the switcher now, invisibly; it's revealed when the build finishes (no delay)
            // or when the show delay ends, whichever is later.
            if (Settings.ShowDelayMs > 0) _showTimer.Start();
            else _revealRequested = true;
            PrepareUi();
        }
        catch (Exception ex)
        {
            Log.Error("begin session", ex);
            End();
        }
    }

    /// <summary>
    /// Decides which monitors show a switcher and which tiles each one lists, based on
    /// showOn and listWindowsFrom. Sets _plan, _items, _columns and _focusOverlay.
    /// </summary>
    private void PlanSession(OverlayHost? keepFocus)
    {
        if (_testItems is not null) // --selftest's synthetic tiles
        {
            PlanForTest(OpenStackView() ?? _testItems);
            return;
        }

        var settings = Settings;
        IntPtr focusMonitor = keepFocus?.Monitor.Handle ?? (settings.ShowOn == ShowOn.MonitorWithActiveWindow
            ? Native.MonitorFromWindow(_originalForeground, Native.MONITOR_DEFAULTTONEAREST)
            : Monitors.UnderCursor());

        var candidates = settings.ShowOn == ShowOn.AllMonitors
            ? _overlays.ToList()
            : _overlays.Where(o => o.Monitor.Handle == focusMonitor).ToList();
        if (candidates.Count == 0) candidates = _overlays.Take(1).ToList();
        var focus = candidates.FirstOrDefault(o => o.Monitor.Handle == focusMonitor) ?? candidates[0];

        var plan = new List<PlanEntry>();
        if (settings.ListWindowsFrom == ListWindowsFrom.AllMonitors)
        {
            // One list and one grid for every monitor: as many columns as the most cramped one fits.
            var items = OpenStackView() ?? Search(Group(_windows));
            int columns = candidates.Min(o => o.MaxColumns(items.Count, settings));
            plan.AddRange(candidates.Select(o => new PlanEntry(o, items, columns, MirrorsSelection: true)));
        }
        else
        {
            // An open stack replaces only the focus monitor's list; the others keep their own windows.
            var stackView = OpenStackView();
            if (stackView is not null) plan.Add(new PlanEntry(focus, stackView, focus.MaxColumns(stackView.Count, settings), MirrorsSelection: true));
            foreach (var host in candidates)
            {
                if (stackView is not null && host == focus) continue;
                var own = WindowFilter.ForMonitor(_windows, ListWindowsFrom.ThisMonitor, host.Monitor.Handle);
                // A monitor with no windows of its own: the focus monitor falls back to every window
                // (so Alt+Tab still works), the others simply don't show a switcher.
                if (own.Count == 0 && host == focus) own = _windows;
                var items = Search(Group(own));
                // While searching, the focus monitor stays up (showing "no match") so typing can continue.
                if (items.Count == 0 && host != focus) continue;
                plan.Add(new PlanEntry(host, items, host.MaxColumns(items.Count, settings), MirrorsSelection: host == focus));
            }
        }

        _plan = plan;
        _plannedQuery = _query;
        var focusEntry = plan.FirstOrDefault(p => p.Host == focus) ?? plan.FirstOrDefault();
        _focusOverlay = focusEntry?.Host;
        _items = focusEntry?.Items ?? new List<SwitcherItem>();
        _columns = focusEntry?.Columns ?? 1;
    }

    /// <summary>
    /// The open stack's tiles (Back, then its windows that are still listed and match the search),
    /// or null when no stack is open. A stack with no windows left is closed.
    /// </summary>
    private List<SwitcherItem>? OpenStackView()
    {
        if (_openStack is null) return null;
        var windows = WindowGrouping.StackView(_openStack, w => _windows.Any(x => x.Handle == w.Handle)).Skip(1).ToList();
        windows = Search(windows);
        if (windows.Count == 0)
        {
            _openStack = null;
            return null;
        }
        windows.Insert(0, SwitcherItem.CreateBack());
        return windows;
    }

    private List<SwitcherItem> Group(IReadOnlyList<WindowInfo> windows) =>
        WindowGrouping.Build(windows, Settings.GroupWindows, WindowEnumerator.AppKey);

    private List<SwitcherItem> Search(List<SwitcherItem> items) =>
        _query.Length == 0 ? items : WindowSearch.Filter(items, _query, Settings.SearchMode, SearchTextOf);

    /// <summary>Title plus app name. App names need a process lookup, so they're only built once someone types.</summary>
    private string SearchTextOf(WindowInfo window)
    {
        _searchText ??= new Dictionary<IntPtr, string>();
        if (!_searchText.TryGetValue(window.Handle, out string? text))
        {
            text = window.Title + " " + WindowSearch.AppName(window.AppUserModelId, WindowEnumerator.ProcessPathOf(window));
            _searchText[window.Handle] = text;
        }
        return text;
    }

    /// <summary>The search changed (typing, Backspace, Esc): re-filter every switcher.</summary>
    private void SetQuery(List<string> strokes)
    {
        if (!_active) return;
        string query = string.Concat(strokes);
        if (query.Length > 200) return;  // nobody types that much; don't let a stuck key grow it forever
        _searchStrokes = strokes;
        _query = query;
        _openStack = null;               // typing searches every window, not just the open stack
        if (!_uiShown) RevealUi();       // typing before the switcher appeared: show it now
        if (!_uiPrepared) return;        // PrepareUi applies the query when the build finishes
        Replan(resetSelection: true);
        RestartStackTimer();
    }

    /// <summary>
    /// Re-plans the session (after a search change or a closed window) and updates every switcher
    /// in place: monitors that no longer list anything hide, monitors that do again reappear.
    /// </summary>
    private void Replan(bool resetSelection) =>
        Replan(items => resetSelection ? 0 : Math.Clamp(_selected, 0, Math.Max(0, items.Count - 1)));

    /// <param name="select">Picks the selection from the new focus list.</param>
    private void Replan(Func<List<SwitcherItem>, int> select)
    {
        var previous = _plan;
        PlanSession(keepFocus: _focusOverlay);
        _selected = Math.Clamp(select(_items), 0, Math.Max(0, _items.Count - 1));
        _hook.BackSelected = SelectedIsBack;
        var settings = Settings;
        foreach (var gone in previous.Where(p => _plan.All(n => n.Host != p.Host))) gone.Host.Hide();
        foreach (var entry in _plan)
        {
            entry.Host.Update(entry.Items, entry.MirrorsSelection ? _selected : -1, settings, entry.Columns, _query);
            // A monitor coming back mid-session may still be cloaked from an earlier build.
            if (_uiShown && previous.All(p => p.Host != entry.Host)) entry.Host.Reveal();
        }
    }

    /// <summary>Builds every planned overlay in parallel (each on its own thread), cloaked.</summary>
    private async void PrepareUi()
    {
        if (!_active || _preparing || _uiPrepared || _plan.Count == 0) return;
        int session = _sessionId;
        _preparing = true;

        try
        {
            var settings = Settings;
            var plan = _plan;
            int selected = _selected;
            string query = _plannedQuery;
            await Task.WhenAll(plan.Select(p => p.Host.PrepareAsync(p.Items, p.MirrorsSelection ? selected : -1, settings, p.Columns, query)));
            _timing?.Mark("overlays built and drawn");

            if (session != _sessionId || !_active) return; // session ended meanwhile; End() hid them
            _uiPrepared = true;
            if (_query != query) Replan(resetSelection: true);    // typed while building
            else if (_selected != selected) Select(_selected);    // selection moved while building
            if (_stackRequested)
            {
                _stackRequested = false;
                OpenStack();
            }
            if (_revealRequested) RevealUi();
        }
        catch (Exception ex)
        {
            Log.Error("prepare ui", ex);
            if (session == _sessionId) End();
        }
        finally
        {
            if (session == _sessionId) _preparing = false;
        }
    }

    /// <summary>Shows the overlays, or asks for that to happen as soon as they're built.</summary>
    private void RevealUi()
    {
        _showTimer.Stop();
        if (!_active) return;
        if (!_uiPrepared)
        {
            _revealRequested = true;
            PrepareUi(); // no-op if already building
            return;
        }
        if (_uiShown) return;

        _timing?.Mark("reveal");
        foreach (var entry in _plan) entry.Host.Reveal();
        _uiShown = true;
        RestartStackTimer(); // resting on a stack only counts once the switcher can be seen

        // Taking focus ourselves means (a) the app underneath can't react to the Alt release and
        // (b) we're the foreground process, so Windows lets us activate the chosen window later.
        if (_focusOverlay is not null) WindowActivator.Activate(_focusOverlay.Handle);
        _timing?.Mark("activated");
    }

    private async Task WaitUntilShown(int timeoutMs = 3000)
    {
        for (int waited = 0; _active && !_uiShown && waited < timeoutMs; waited += 2) await Task.Delay(2);
    }

    /// <summary>Build and show, then wait until visible (demo and self-test).</summary>
    private async Task ShowUiAsync()
    {
        _revealRequested = true;
        PrepareUi();
        await WaitUntilShown();
    }

    private void EnsureOverlays()
    {
        var monitors = Monitors.GetAll();
        string key = string.Join(";", monitors.Select(m => m.Key));
        if (key == _monitorLayoutKey && _overlays.Count == monitors.Count) return;

        DiscardOverlays();
        foreach (var monitor in monitors)
        {
            var overlay = new OverlayHost(monitor, _dispatcher);
            overlay.TileClicked += index => OnTileClicked(overlay, index);
            overlay.TileCloseRequested += index => OnTileCloseRequested(overlay, index);
            overlay.TileHovered += index => OnTileHovered(overlay, index);
            overlay.TileBadgeClicked += index => OnTileBadgeClicked(overlay, index);
            overlay.Deactivated += OnOverlayDeactivated;
            _overlays.Add(overlay);
        }
        _monitorLayoutKey = key;
        Log.Info($"overlays rebuilt for {monitors.Count} monitor(s)");
    }

    /// <summary>Destroys the overlay windows and their threads; the next Alt+Tab recreates them.</summary>
    private void DiscardOverlays()
    {
        foreach (var overlay in _overlays)
        {
            try { overlay.Close(); }
            catch (Exception ex) { Log.Error("close overlay", ex); }
        }
        _overlays.Clear();
        _monitorLayoutKey = "";
    }

    private SwitcherItem? ItemAt(OverlayHost host, int index)
    {
        var entry = _plan.FirstOrDefault(p => p.Host == host);
        return entry is not null && index >= 0 && index < entry.Items.Count ? entry.Items[index] : null;
    }

    private void OnTileClicked(OverlayHost host, int index)
    {
        if (!_active) return;
        if (ItemAt(host, index) is { IsBack: true })
        {
            CloseStack();
            return;
        }
        if (host == _focusOverlay)
        {
            _selected = index;
            Commit();
            return;
        }
        // A click on another monitor's own list (listWindowsFrom = thisMonitor).
        if (ItemAt(host, index) is { } item)
        {
            WindowActivator.Activate(item.Shown.Handle);
            End();
        }
    }

    private void OnTileCloseRequested(OverlayHost host, int index)
    {
        if (_active && ItemAt(host, index) is { IsBack: false } item) CloseWindow(item.Shown.Handle);
    }

    /// <summary>A stack's badge: open the stack (on a switcher the keyboard drives; elsewhere it's a plain click).</summary>
    private void OnTileBadgeClicked(OverlayHost host, int index)
    {
        if (!_active) return;
        if (_plan.FirstOrDefault(p => p.Host == host) is not { MirrorsSelection: true })
        {
            OnTileClicked(host, index);
            return;
        }
        if (index < 0 || index >= _items.Count) return;
        Select(index);
        OpenStack();
    }

    /// <summary>hoverSelects: the mouse moved onto a tile of the keyboard-driven switcher.</summary>
    private void OnTileHovered(OverlayHost host, int index)
    {
        if (_active && host == _focusOverlay && index != _selected && index < _items.Count) Select(index);
    }

    /// <summary>Delete key: close the selected window.</summary>
    private void CloseSelected()
    {
        if (_active && _selected >= 0 && _selected < _items.Count && !_items[_selected].IsBack) CloseWindow(_items[_selected].Shown.Handle);
    }

    /// <summary>
    /// Asks the window to close (WM_CLOSE, like clicking its ×, so apps can still ask to save) and
    /// takes it out of the switcher. The switcher stays open on the same monitor.
    /// </summary>
    private void CloseWindow(IntPtr hwnd)
    {
        if (!_uiPrepared) return; // too early: tiles aren't built yet
        Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Log.Info("closed a window from the switcher");

        _windows.RemoveAll(w => w.Handle == hwnd);
        if (_windows.Count == 0)
        {
            End();
            return;
        }
        bool stackWasOpen = _openStack is not null;
        // Closing an open stack's last window shows every window again, with the stack's old place selected.
        Replan(items => stackWasOpen && _openStack is null ? _stackReturnIndex : Math.Clamp(_selected, 0, Math.Max(0, items.Count - 1)));
    }

    /// <summary>The user clicked outside the switcher: close it (mainly for stay-open mode).</summary>
    private void OnOverlayDeactivated()
    {
        // Deferred: focus may be moving to another of our overlays (a click on another monitor),
        // or we may be mid-Commit, which deactivates the overlay on purpose.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_active || !_uiShown || _traceEnds) return; // (the self-test must not be ended by the user's clicks)
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            End();
        }));
    }

    private void Move(int delta) => Select(GridNavigation.Step(_selected, delta, _items.Count));

    private void MoveRow(int delta) => Select(GridNavigation.Row(_selected, delta, _items.Count, _columns));

    private void Jump(JumpKey key)
    {
        if (!_active || _items.Count == 0) return;
        int rows = _focusOverlay?.RowsPerPage(_items.Count, Settings, _columns) ?? 1;
        Select(key switch
        {
            JumpKey.Home => 0,
            JumpKey.End => _items.Count - 1,
            JumpKey.PageUp => GridNavigation.Page(_selected, -1, _items.Count, _columns, rows),
            _ => GridNavigation.Page(_selected, +1, _items.Count, _columns, rows),
        });
    }

    /// <summary>1–9: select or switch to that tile.</summary>
    private void OnNumber(int number)
    {
        int index = SwitcherItem.IndexOfNumber(_items, number); // past an open stack's Back tile
        if (!_active || index < 0) return;
        if (Settings.NumberKeys == NumberKeys.Switch)
        {
            _selected = index;
            Commit();
        }
        else
        {
            Select(index);
            if (!_uiShown) RevealUi();
        }
    }

    private void Select(int index)
    {
        if (!_active || _items.Count == 0) return;
        bool moved = index != _selected;
        _selected = index;
        _hook.BackSelected = SelectedIsBack;
        foreach (var entry in _plan)
            if (entry.MirrorsSelection) entry.Host.Select(_selected);
        if (moved) RestartStackTimer();
    }

    private bool SelectedIsBack => _selected >= 0 && _selected < _items.Count && _items[_selected].IsBack;

    // ---- App stacks (groupWindows = stacked) ----

    /// <summary>The key above Tab: open the selected stack, or move within an open one (Shift: backwards).</summary>
    private void OnStackKey(int delta)
    {
        if (!_active) return;
        if (_openStack is not null) Move(delta);
        else if (!_uiPrepared) _stackRequested = true; // pressed before the tiles were built
        else OpenStack();
    }

    /// <summary>
    /// Shows the selected stack's windows as tiles, after a Back tile. The window the stack's tile
    /// showed stays selected, so letting go of Alt picks the same window as before it opened.
    /// </summary>
    private void OpenStack()
    {
        if (!_active || !_uiPrepared || _openStack is not null || _selected < 0 || _selected >= _items.Count) return;
        var stack = _items[_selected];
        if (!stack.IsStack) return;
        _stackTimer.Stop();
        IntPtr shown = stack.Shown.Handle;
        _openStack = stack;
        _stackReturnIndex = _selected;
        Replan(items => Math.Max(1, items.FindIndex(i => !i.IsBack && i.Shown.Handle == shown)));
        if (!_uiShown) RevealUi(); // opened before the switcher appeared: show it now
    }

    /// <summary>Back: every window again, with the stack's tile selected. It doesn't reopen until the selection moves.</summary>
    private void CloseStack()
    {
        if (!_active || _openStack is null) return;
        _stackTimer.Stop();
        var stack = _openStack;
        _openStack = null;
        Replan(items =>
        {
            bool IsTheStack(SwitcherItem i) => stack.Windows.Any(w => i.Contains(w.Handle));
            // Where it was, unless a closed window moved it; then wherever it is now.
            if (_stackReturnIndex < items.Count && IsTheStack(items[_stackReturnIndex])) return _stackReturnIndex;
            int index = items.FindIndex(IsTheStack);
            return index >= 0 ? index : _stackReturnIndex;
        });
    }

    /// <summary>(Re)starts the rest timer when the selection is a stack (or, in an open stack, its Back tile).</summary>
    private void RestartStackTimer()
    {
        _stackTimer.Stop();
        if (!_active || !_uiShown || _stackDelayMs <= 0 || _selected < 0 || _selected >= _items.Count) return;
        var item = _items[_selected];
        // Not while searching: pauses between keystrokes would keep opening stacks.
        bool due = _openStack is null ? item.IsStack && _query.Length == 0 : item.IsBack;
        if (!due) return;
        _stackTimer.Interval = TimeSpan.FromMilliseconds(_stackDelayMs);
        _stackTimer.Start();
    }

    private void OnStackTimer()
    {
        _stackTimer.Stop();
        if (!_active || !_uiShown) return;
        if (_openStack is null) OpenStack();
        else if (SelectedIsBack) CloseStack();
    }

    private void OnModifiersReleased()
    {
        // Stay-open mode only kicks in once the switcher is visible; a quick tap still switches.
        if (Settings.StayOpenAfterAltRelease && _uiShown) return;
        Commit();
    }

    private void Commit()
    {
        if (!_active) return;
        if (SelectedIsBack)
        {
            Cancel(); // let go of Alt on Back: nothing was picked, go back where we came from
            return;
        }
        IntPtr target = _selected >= 0 && _selected < _items.Count ? _items[_selected].Shown.Handle : IntPtr.Zero;
        // Nothing selected (a search with no match): go back where we came from, like Esc.
        if (target == IntPtr.Zero && _uiShown) target = _originalForeground;
        // Activate before hiding the overlays: while an overlay has focus we're allowed to hand it on.
        WindowActivator.Activate(target);
        End();
    }

    private void Cancel()
    {
        if (!_active) return;
        if (_uiShown) WindowActivator.Activate(_originalForeground);
        End();
    }

    /// <summary>--selftest: log who ends sessions, to diagnose unexpected ends.</summary>
    private bool _traceEnds;

    private void End()
    {
        if (_traceEnds && _active) Log.Info("selftest: session ended by " + Environment.StackTrace.Split('\n').Skip(2).FirstOrDefault()?.Trim());
        _active = false;
        _sessionId++;
        _preparing = false;
        _revealRequested = false;
        _showTimer.Stop();
        _stackTimer.Stop();
        _openStack = null;
        _stackRequested = false;
        _testItems = null;
        _hook.EndSession();
        _focusOverlay = null;
        _sessionSettings = null;
        _searchStrokes = new List<string>();
        _query = _plannedQuery = "";
        _searchText = null;
        var plan = _plan;
        _plan = new List<PlanEntry>();
        if (plan.Count == 0) return;

        foreach (var entry in plan) entry.Host.EndSession();
        _uiPrepared = false;
        _uiShown = false;
        // Drop references to this session's icons and titles.
        _items = new List<SwitcherItem>();
        _windows = new List<WindowInfo>();
        _trimmer.Schedule();
    }

    /// <summary>Set only by --measure, to time the stages of one open.</summary>
    private OpenTiming? _timing;

    private sealed class OpenTiming
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public List<(string Stage, double Ms)> Marks { get; } = new();
        public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;
        public void Mark(string stage) => Marks.Add((stage, _sw.Elapsed.TotalMilliseconds));
    }

    public void Dispose()
    {
        _store.Changed -= ApplySettings;
        _hook.Dispose();
        _showTimer.Stop();
        _stackTimer.Stop();
        _displayChangeTimer.Stop();
        DiscardOverlays();
    }
}
