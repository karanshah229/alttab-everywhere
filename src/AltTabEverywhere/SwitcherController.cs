using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// Owns one Alt+Tab session: the tiles (windows or app stacks), selection, and the per-monitor
/// overlays. Everything here runs on the UI thread.
/// </summary>
internal sealed partial class SwitcherController : IDisposable
{
    private readonly SettingsStore _store;
    private readonly KeyboardHook _hook = new();
    private readonly DispatcherTimer _showTimer;
    private readonly MemoryTrimmer _trimmer = new(TimeSpan.FromSeconds(2));
    private readonly List<SwitcherWindow> _overlays = new();
    private string _monitorLayoutKey = "";

    private List<SwitcherItem> _items = new();
    private int _selected;
    private bool _active;
    private bool _uiPrepared; // overlays built and laid out (cloaked until revealed)
    private bool _uiShown;    // overlays visible
    private List<SwitcherWindow> _prepared = new();
    private IntPtr _originalForeground;
    private SwitcherWindow? _focusOverlay;

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

    private bool _warmingUp;

    /// <summary>
    /// Builds the switcher once, cloaked, right after startup, so the first real Alt+Tab doesn't pay
    /// for compiling the code (~600 ms -> normal speed); then trims memory. Nothing is shown.
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
                _selected = 0;
                PrepareUi();
                await Dispatcher.Yield(DispatcherPriority.Background); // let it render (still cloaked)
                await System.Threading.Tasks.Task.Delay(200);
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

    private void ApplySettings()
    {
        _hook.StayOpen = Settings.StayOpenAfterAltRelease;
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.ShowDelayMs));
    }

    /// <summary>--demo: open the switcher without the keyboard, log the overlay layout, then close.</summary>
    public void RunDemo(TimeSpan duration, Action done)
    {
        BeginSession(reverse: false, sendMaskKey: false);
        ShowUi();

        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        logTimer.Tick += (_, _) =>
        {
            logTimer.Stop();
            Log.Info($"demo: tiles={_items.Count} windows={_items.Sum(i => i.Count)} stacks={_items.Count(i => i.IsStack)} " +
                     $"selected={_selected} overlays={_overlays.Count} columns={_focusOverlay?.ColumnCount}");
            foreach (var overlay in _overlays) Log.Info("demo: " + overlay.DescribeForLog());
        };
        logTimer.Start();

        var endTimer = new DispatcherTimer { Interval = duration };
        endTimer.Tick += (_, _) =>
        {
            endTimer.Stop();
            Cancel();
            done();
        };
        endTimer.Start();
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
            // Like Windows: start on the previous tile, unless focus isn't on a listed window.
            int forwardStart = _items.Count > 1 && _items[0].Contains(_originalForeground) ? 1 : 0;
            _selected = reverse ? _items.Count - 1 : forwardStart;

            // Build the switcher now, invisibly, so it's ready the moment the show delay ends.
            if (Settings.ShowDelayMs > 0) _showTimer.Start();
            PrepareUi();
            // No delay: reveal right after WPF has drawn the first frame (Background runs after
            // Render), so the switcher never appears as an empty box.
            if (Settings.ShowDelayMs <= 0)
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RevealUi));
        }
        catch (Exception ex)
        {
            Log.Error("begin session", ex);
            End();
        }
    }

    /// <summary>Builds and lays out the overlays, cloaked (rendered but not shown).</summary>
    private void PrepareUi()
    {
        if (!_active || _uiPrepared) return;

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
            int columns = shown.Min(o => o.MaxColumns(_items.Count, settings));
            foreach (var overlay in shown)
            {
                overlay.SetCloaked(true);
                overlay.ShowSession(_items, _selected, settings, columns);
                _timing?.Mark($"built {overlay.Monitor.DeviceName.Replace(@"\\.\", "")}");
            }
            _prepared = shown;
            _uiPrepared = true;
            _focusOverlay = shown.FirstOrDefault(o => o.Monitor.Handle == cursorMonitor) ?? shown.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error("prepare ui", ex);
            End();
        }
    }

    /// <summary>Shows the (already built) overlays: called when the show delay ends.</summary>
    private void RevealUi()
    {
        _showTimer.Stop();
        if (!_active) return;
        if (!_uiPrepared) PrepareUi();
        if (!_uiPrepared || _uiShown) return;

        _timing?.Mark("reveal start");
        foreach (var overlay in _prepared) overlay.SetCloaked(false);
        _uiShown = true;
        _timing?.Mark("uncloaked");

        // Taking focus ourselves means (a) the app underneath can't react to the Alt release and
        // (b) we're the foreground process, so Windows lets us activate the chosen window later.
        if (_focusOverlay is not null) WindowActivator.Activate(_focusOverlay.Handle);
        _timing?.Mark("activated");
    }

    /// <summary>Set only by --measure, to time the stages of one open.</summary>
    private OpenTiming? _timing;

    private sealed class OpenTiming
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public List<(string Stage, double Ms)> Marks { get; } = new();
        public void Mark(string stage) => Marks.Add((stage, _sw.Elapsed.TotalMilliseconds));
    }

    /// <summary>Build and show at once (demo and self-test).</summary>
    private void ShowUi()
    {
        PrepareUi();
        RevealUi();
    }

    private void EnsureOverlays()
    {
        var monitors = Monitors.GetAll();
        string key = string.Join(";", monitors.Select(m => m.Key));
        if (key == _monitorLayoutKey && _overlays.Count == monitors.Count) return;

        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();

        foreach (var monitor in monitors)
        {
            var overlay = new SwitcherWindow(monitor);
            overlay.TileClicked += index =>
            {
                _selected = index;
                Commit();
            };
            overlay.Deactivated += (_, _) => OnOverlayDeactivated();
            _overlays.Add(overlay);
        }
        _monitorLayoutKey = key;
        Log.Info($"overlays rebuilt for {monitors.Count} monitor(s)");
    }

    /// <summary>Destroys the overlay windows; the next Alt+Tab recreates them.</summary>
    private void DiscardOverlays()
    {
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
        _monitorLayoutKey = "";
    }

    /// <summary>The user clicked outside the switcher: close it (mainly for stay-open mode).</summary>
    private void OnOverlayDeactivated()
    {
        // Deferred: focus may be moving to another of our overlays (a click on another monitor),
        // or we may be mid-Commit, which deactivates the overlay on purpose.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_active || !_uiShown) return;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            End();
        }));
    }

    private void Move(int delta) => Select(GridNavigation.Step(_selected, delta, _items.Count));

    private void MoveRow(int delta)
    {
        int columns = _focusOverlay?.ColumnCount ?? _items.Count;
        Select(GridNavigation.Row(_selected, delta, _items.Count, columns));
    }

    private void Select(int index)
    {
        if (!_active || _items.Count == 0) return;
        _selected = index;
        if (!_uiPrepared) return;
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.SetSelected(_selected);
    }

    /// <summary>Alt+` / Alt+Shift+`: show the next / previous window inside the selected stack.</summary>
    private void CycleStack(int delta)
    {
        if (!_active || _selected < 0 || _selected >= _items.Count) return;
        var item = _items[_selected];
        if (!item.IsStack) return;
        if (!_uiShown) RevealUi(); // pressed before the switcher appeared: show it now
        item.Cycle(delta);
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.RefreshTile(_selected, item);
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
        _showTimer.Stop();
        _hook.EndSession();
        _focusOverlay = null;
        if (!_uiPrepared) return;
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.EndSession();
        _uiPrepared = false;
        _uiShown = false;
        _prepared = new List<SwitcherWindow>();
        _items = new List<SwitcherItem>(); // drop references to this session's icons and titles
        _trimmer.Schedule();
    }

    public void Dispose()
    {
        _store.Changed -= ApplySettings;
        _hook.Dispose();
        _showTimer.Stop();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }
}
