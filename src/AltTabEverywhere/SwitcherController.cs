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
internal sealed class SwitcherController : IDisposable
{
    private readonly Settings _settings;
    private readonly KeyboardHook _hook = new();
    private readonly DispatcherTimer _showTimer;
    private readonly List<SwitcherWindow> _overlays = new();
    private string _monitorLayoutKey = "";

    private List<WindowInfo> _windows = new();
    private int _selected;
    private bool _active;
    private bool _uiShown;
    private IntPtr _originalForeground;

    public SwitcherController(Settings settings)
    {
        _settings = settings;
        _showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, settings.ShowDelayMs)) };
        _showTimer.Tick += (_, _) => ShowUi();

        _hook.Tab += OnTab;
        _hook.Move += Move;
        _hook.Commit += Commit;
        _hook.Cancel += Cancel;
    }

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

    /// <summary>--demo: open the switcher without the keyboard, log the overlay layout, then close.</summary>
    public void RunDemo(TimeSpan duration, Action done)
    {
        BeginSession(reverse: false, sendMaskKey: false);
        ShowUi();

        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        logTimer.Tick += (_, _) =>
        {
            logTimer.Stop();
            Log.Info($"demo: windows={_windows.Count} selected={_selected} overlays={_overlays.Count}");
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

            if (_settings.ShowDelayMs <= 0) ShowUi();
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
            EnsureOverlays();
            IntPtr cursorMonitor = Monitors.UnderCursor();
            var shown = new List<SwitcherWindow>();
            foreach (var overlay in _overlays)
            {
                if (!_settings.ShowOnAllMonitors && overlay.Monitor.Handle != cursorMonitor) continue;
                overlay.ShowSession(_windows, _selected);
                shown.Add(overlay);
            }
            _uiShown = true;

            // Taking focus ourselves means (a) the app underneath can't react to the Alt release and
            // (b) we're the foreground process, so Windows lets us activate the chosen window later.
            var focusOverlay = shown.FirstOrDefault(o => o.Monitor.Handle == cursorMonitor) ?? shown.FirstOrDefault();
            if (focusOverlay is not null) WindowActivator.Activate(focusOverlay.Handle);
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
            var overlay = new SwitcherWindow(monitor, _settings);
            overlay.TileClicked += index =>
            {
                _selected = index;
                Commit();
            };
            _overlays.Add(overlay);
        }
        _monitorLayoutKey = key;
        Log.Info($"overlays rebuilt for {monitors.Count} monitor(s)");
    }

    private void Move(int delta)
    {
        if (!_active || _windows.Count == 0) return;
        int count = _windows.Count;
        _selected = ((_selected + delta) % count + count) % count;
        if (!_uiShown) return;
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.SetSelected(_selected);
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
        if (!_uiShown) return;
        foreach (var overlay in _overlays)
            if (overlay.IsVisible) overlay.EndSession();
        _uiShown = false;
    }

    public void Dispose()
    {
        _hook.Dispose();
        _showTimer.Stop();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }
}
