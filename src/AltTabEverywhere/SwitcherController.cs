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

    private sealed record PlanEntry(OverlayHost Host, List<SwitcherItem> Items, int Columns, bool MirrorsSelection);

    public SwitcherController(SettingsStore store)
    {
        _store = store;
        _showTimer = new DispatcherTimer();
        _showTimer.Tick += (_, _) => RevealUi();
        _displayChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            if (!_active) WarmUpThenTrim(); // rebuild overlays for the new layout before the next Alt+Tab
        };

        _hook.Tab += OnTab;
        _hook.Move += Move;
        _hook.MoveRow += MoveRow;
        _hook.CycleStack += CycleStack;
        _hook.CloseSelected += CloseSelected;
        _hook.AltReleased += OnAltReleased;
        _hook.Commit += Commit;
        _hook.Cancel += Cancel;

        _store.Changed += ApplySettings;
        ApplySettings();
    }

    private Settings Settings => _settingsOverride ?? _store.Current;

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
        _hook.StayOpen = Settings.StayOpenAfterAltRelease;
        _hook.DeleteCloses = Settings.DeleteKeyCloses;
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.ShowDelayMs));
    }

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

    private void OnTab(bool isFirst, bool reverse)
    {
        if (!isFirst && _active)
        {
            Move(reverse ? -1 : +1);
            return;
        }
        BeginSession(reverse, sendMaskKey: true);
    }

    /// <summary>Lists the session's windows (with exclusions). Returns how many.</summary>
    private int LoadWindows()
    {
        var settings = Settings;
        var windows = WindowEnumerator.GetSwitchableWindows(settings.VirtualDesktops == VirtualDesktops.All);
        _windows = WindowFilter.ApplyExclusions(windows, settings, WindowEnumerator.ProcessPathOf);
        return _windows.Count;
    }

    private void BeginSession(bool reverse, bool sendMaskKey)
    {
        try
        {
            if (_warmingUp)
            {
                _warmingUp = false; // a real Alt+Tab arrived during warm-up: drop the warm-up session
                End();
            }
            _trimmer.Cancel();
            if (sendMaskKey) WindowActivator.SendMenuMaskKey();
            _timing?.Mark("begin");
            _originalForeground = Native.GetForegroundWindow();
            if (LoadWindows() == 0)
            {
                _hook.EndSession();
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
            var items = Group(_windows);
            int columns = candidates.Min(o => o.MaxColumns(items.Count, settings));
            plan.AddRange(candidates.Select(o => new PlanEntry(o, items, columns, MirrorsSelection: true)));
        }
        else
        {
            foreach (var host in candidates)
            {
                var items = Group(WindowFilter.ForMonitor(_windows, ListWindowsFrom.ThisMonitor, host.Monitor.Handle));
                // A monitor with no windows of its own: the focus monitor falls back to every window
                // (so Alt+Tab still works), the others simply don't show a switcher.
                if (items.Count == 0 && host == focus) items = Group(_windows);
                if (items.Count == 0) continue;
                plan.Add(new PlanEntry(host, items, host.MaxColumns(items.Count, settings), MirrorsSelection: host == focus));
            }
        }

        _plan = plan;
        var focusEntry = plan.FirstOrDefault(p => p.Host == focus) ?? plan.FirstOrDefault();
        _focusOverlay = focusEntry?.Host;
        _items = focusEntry?.Items ?? new List<SwitcherItem>();
        _columns = focusEntry?.Columns ?? 1;
    }

    private List<SwitcherItem> Group(IReadOnlyList<WindowInfo> windows) =>
        WindowGrouping.Build(windows, Settings.GroupWindows, WindowEnumerator.AppKey);

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
            await Task.WhenAll(plan.Select(p => p.Host.PrepareAsync(p.Items, p.MirrorsSelection ? selected : -1, settings, p.Columns)));
            _timing?.Mark("overlays built and drawn");

            if (session != _sessionId || !_active) return; // session ended meanwhile; End() hid them
            _uiPrepared = true;
            if (_selected != selected) Select(_selected); // selection moved while building
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
        if (_active && ItemAt(host, index) is { } item) CloseWindow(item.Shown.Handle);
    }

    /// <summary>Delete key: close the selected window.</summary>
    private void CloseSelected()
    {
        if (_active && _selected >= 0 && _selected < _items.Count) CloseWindow(_items[_selected].Shown.Handle);
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

        var previous = _plan;
        PlanSession(keepFocus: _focusOverlay);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _items.Count - 1));
        var settings = Settings;
        foreach (var gone in previous.Where(p => _plan.All(n => n.Host != p.Host))) gone.Host.Hide();
        foreach (var entry in _plan) entry.Host.Update(entry.Items, entry.MirrorsSelection ? _selected : -1, settings, entry.Columns);
        if (_items.Count == 0) End();
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

    private void Select(int index)
    {
        if (!_active || _items.Count == 0) return;
        _selected = index;
        foreach (var entry in _plan)
            if (entry.MirrorsSelection) entry.Host.Select(_selected);
    }

    /// <summary>Alt+` / Alt+Shift+`: show the next / previous window inside the selected stack.</summary>
    private void CycleStack(int delta)
    {
        if (!_active || _selected < 0 || _selected >= _items.Count) return;
        var item = _items[_selected];
        if (!item.IsStack) return;
        item.Cycle(delta);
        foreach (var entry in _plan)
            if (entry.MirrorsSelection) entry.Host.Refresh(_selected, item);
        if (!_uiShown) RevealUi(); // pressed before the switcher appeared: show it now
    }

    private void OnAltReleased()
    {
        // Stay-open mode only kicks in once the switcher is visible; a quick tap still switches.
        if (Settings.StayOpenAfterAltRelease && _uiShown) return;
        Commit();
    }

    private void Commit()
    {
        if (!_active) return;
        IntPtr target = _selected >= 0 && _selected < _items.Count ? _items[_selected].Shown.Handle : IntPtr.Zero;
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
        _hook.EndSession();
        _focusOverlay = null;
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
        _displayChangeTimer.Stop();
        DiscardOverlays();
    }
}
