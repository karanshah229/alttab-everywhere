using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere;

/// <summary>
/// Owns one Alt+Tab session: window list, selection, and the per-monitor overlays.
/// Everything here runs on the UI thread.
/// </summary>
internal sealed partial class SwitcherController : IDisposable
{
    private readonly SettingsStore _store;
    private readonly KeyboardHook _hook = new();
    private readonly DispatcherTimer _showTimer;
    private readonly List<SwitcherWindow> _overlays = new();
    private string _monitorLayoutKey = "";

    private List<WindowInfo> _windows = new();
    private int _selected;
    private bool _active;
    private bool _uiShown;
    private IntPtr _originalForeground;
    private SwitcherWindow? _focusOverlay;

    public SwitcherController(SettingsStore store)
    {
        _store = store;
        _showTimer = new DispatcherTimer();
        _showTimer.Tick += (_, _) => ShowUi();

        _hook.Tab += OnTab;
        _hook.Move += Move;
        _hook.MoveRow += MoveRow;
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

    /// <summary>--demo: open the switcher without the keyboard, log the overlay layout, then close.</summary>
    public void RunDemo(TimeSpan duration, Action done)
    {
        BeginSession(reverse: false, sendMaskKey: false);
        ShowUi();

        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        logTimer.Tick += (_, _) =>
        {
            logTimer.Stop();
            Log.Info($"demo: windows={_windows.Count} selected={_selected} overlays={_overlays.Count} " +
                     $"columns={_focusOverlay?.ColumnCount}");
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
            if (sendMaskKey) WindowActivator.SendMenuMaskKey();
            _originalForeground = Native.GetForegroundWindow();
            _windows = WindowEnumerator.GetSwitchableWindows();
            if (_windows.Count == 0)
            {
                _hook.EndSession();
                return;
            }

            _active = true;
            // Like Windows: start on the previous window, unless focus isn't on a listed window.
            int forwardStart = _windows.Count > 1 && _windows[0].Handle == _originalForeground ? 1 : 0;
            _selected = reverse ? _windows.Count - 1 : forwardStart;

            if (Settings.ShowDelayMs <= 0) ShowUi();
            else _showTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("begin session", ex);
            End();
        }
    }

    private void ShowUi()
    {
        _showTimer.Stop();
        if (!_active || _uiShown) return;

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
            int columns = shown.Min(o => o.MaxColumns(_windows.Count, settings));
            foreach (var overlay in shown) overlay.ShowSession(_windows, _selected, settings, columns);
            _uiShown = true;

            // Taking focus ourselves means (a) the app underneath can't react to the Alt release and
            // (b) we're the foreground process, so Windows lets us activate the chosen window later.
            // The focused overlay's grid also defines what Up/Down means.
            _focusOverlay = shown.FirstOrDefault(o => o.Monitor.Handle == cursorMonitor) ?? shown.FirstOrDefault();
            if (_focusOverlay is not null) WindowActivator.Activate(_focusOverlay.Handle);
        }
        catch (Exception ex)
        {
            Log.Error("show ui", ex);
            End();
        }
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

    private void Move(int delta) => Select(GridNavigation.Step(_selected, delta, _windows.Count));

    private void MoveRow(int delta)
    {
        int columns = _focusOverlay?.ColumnCount ?? _windows.Count;
        Select(GridNavigation.Row(_selected, delta, _windows.Count, columns));
    }

    private void Select(int index)
    {
        if (!_active || _windows.Count == 0) return;
        _selected = index;
        if (!_uiShown) return;
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.SetSelected(_selected);
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
        IntPtr target = _selected >= 0 && _selected < _windows.Count ? _windows[_selected].Handle : IntPtr.Zero;
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
        if (!_uiShown) return;
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.EndSession();
        _uiShown = false;
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
