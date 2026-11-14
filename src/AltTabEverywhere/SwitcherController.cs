using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// Owns one Alt+Tab session: the tiles (windows or app stacks), selection, and the per-monitor
/// overlays. Runs on the main UI thread (which also services the keyboard hook); each overlay
/// runs on its own thread via <see cref="OverlayHost"/>.
/// </summary>
internal sealed partial class SwitcherController : IDisposable
{
    private readonly SettingsStore _store;
    private readonly KeyboardHook _hook = new();
    private readonly DispatcherTimer _showTimer;
    private readonly MemoryTrimmer _trimmer = new(TimeSpan.FromSeconds(2));
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<OverlayHost> _overlays = new();
    private string _monitorLayoutKey = "";

    private List<SwitcherItem> _items = new();
    private int _selected;
    private bool _active;
    private int _sessionId;             // bumped on every begin/end, to drop stale async work
    private bool _preparing;            // overlays are being built (cloaked)
    private bool _uiPrepared;           // overlays built and drawn (cloaked until revealed)
    private bool _uiShown;              // overlays visible
    private bool _revealRequested;      // show as soon as the build finishes
    private List<OverlayHost> _prepared = new();
    private int _columns = 1;
    private IntPtr _originalForeground;
    private OverlayHost? _focusOverlay;
    private bool _warmingUp;

    public SwitcherController(SettingsStore store)
    {
        _store = store;
        _showTimer = new DispatcherTimer();
        _showTimer.Tick += (_, _) => RevealUi();

        _hook.Tab += OnTab;
        _hook.Move += Move;
        _hook.MoveRow += MoveRow;
        _hook.CycleStack += CycleStack;
        _hook.AltReleased += OnAltReleased;
        _hook.Commit += Commit;
        _hook.Cancel += Cancel;

        _store.Changed += ApplySettings;
        ApplySettings();
    }

    private Settings Settings => _store.Current;

    public bool Enabled
    {
        get => _hook.Enabled;
        set
        {
            _hook.Enabled = value;
            if (!value) End();
        }
    }

    public void Start() => _hook.Install();

    private void ApplySettings()
    {
        _hook.StayOpen = Settings.StayOpenAfterAltRelease;
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.ShowDelayMs));
    }

    /// <summary>
    /// Builds the switcher once, cloaked, right after startup, so the first real Alt+Tab doesn't pay
    /// for creating the overlay threads and compiling the code; then trims memory. Nothing is shown.
    /// </summary>
    public async void WarmUpThenTrim()
    {
        try
        {
            _items = WindowGrouping.Build(WindowEnumerator.GetSwitchableWindows(), Settings.GroupWindows, WindowEnumerator.AppKey);
            if (_items.Count > 0)
            {
                _warmingUp = true;
                _active = true;
                _sessionId++;
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
        Log.Info($"demo: tiles={_items.Count} windows={_items.Sum(i => i.Count)} stacks={_items.Count(i => i.IsStack)} " +
                 $"selected={_selected} overlays={_overlays.Count} columns={_columns}");
        foreach (var overlay in _overlays) Log.Info("demo: " + overlay.Query(w => w.DescribeForLog()));
        await Task.Delay(duration);
        Cancel();
        done();
    }

    private void OnTab(bool isFirst, bool reverse)
    {
        if (!isFirst && _active)
        {
            Move(reverse ? -1 : +1);
            return;
        }
        BeginSession(reverse, sendMaskKey: true);
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
            var windows = WindowEnumerator.GetSwitchableWindows();
            _timing?.Mark("windows listed");
            _items = WindowGrouping.Build(windows, Settings.GroupWindows, WindowEnumerator.AppKey);
            if (_items.Count == 0)
            {
                _hook.EndSession();
                return;
            }

            _active = true;
            _sessionId++;
            _revealRequested = false;
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

    /// <summary>Builds every overlay in parallel (each on its own thread), cloaked.</summary>
    private async void PrepareUi()
    {
        if (!_active || _preparing || _uiPrepared) return;
        int session = _sessionId;
        _preparing = true;

        try
        {
            var settings = Settings;
            EnsureOverlays();
            IntPtr cursorMonitor = Monitors.UnderCursor();
            var shown = settings.ShowOn == ShowOn.MonitorUnderCursor
                ? _overlays.Where(o => o.Monitor.Handle == cursorMonitor).ToList()
                : _overlays.ToList();
            if (shown.Count == 0) shown = _overlays.Take(1).ToList();

            // One grid for all monitors: as many columns as the most cramped monitor can fit.
            _columns = shown.Min(o => o.MaxColumns(_items.Count, settings));
            _prepared = shown;
            _focusOverlay = shown.FirstOrDefault(o => o.Monitor.Handle == cursorMonitor) ?? shown.FirstOrDefault();

            var items = _items;
            int selected = _selected;
            await Task.WhenAll(shown.Select(o => o.PrepareAsync(items, selected, settings, _columns)));
            _timing?.Mark("overlays built and drawn");

            if (session != _sessionId || !_active) return; // session ended meanwhile; End() hid them
            _uiPrepared = true;
            if (_selected != selected) // selection moved while building
                foreach (var overlay in _prepared) overlay.Select(_selected);
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
        foreach (var overlay in _prepared) overlay.Reveal();
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
            overlay.TileClicked += index =>
            {
                _selected = index;
                Commit();
            };
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

    /// <summary>The user clicked outside the switcher: close it (mainly for stay-open mode).</summary>
    private void OnOverlayDeactivated()
    {
        // Deferred: focus may be moving to another of our overlays (a click on another monitor),
        // or we may be mid-Commit, which deactivates the overlay on purpose.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_active || !_uiShown) return;
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
        foreach (var overlay in _prepared) overlay.Select(_selected);
    }

    /// <summary>Alt+` / Alt+Shift+`: show the next / previous window inside the selected stack.</summary>
    private void CycleStack(int delta)
    {
        if (!_active || _selected < 0 || _selected >= _items.Count) return;
        var item = _items[_selected];
        if (!item.IsStack) return;
        item.Cycle(delta);
        foreach (var overlay in _prepared) overlay.Refresh(_selected, item);
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

    private void End()
    {
        _active = false;
        _sessionId++;
        _preparing = false;
        _revealRequested = false;
        _showTimer.Stop();
        _hook.EndSession();
        _focusOverlay = null;
        if (_prepared.Count == 0) return;

        foreach (var overlay in _prepared) overlay.EndSession();
        _prepared = new List<OverlayHost>();
        _uiPrepared = false;
        _uiShown = false;
        _items = new List<SwitcherItem>(); // drop references to this session's icons and titles
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
        DiscardOverlays();
    }
}
