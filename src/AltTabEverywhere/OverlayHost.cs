using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AltTabEverywhere;

/// <summary>
/// Runs one monitor's <see cref="SwitcherWindow"/> on its own UI thread, so all monitors build and
/// draw their switchers in parallel instead of one after another.
///
/// Every call from the controller is queued onto the overlay's thread; events from the overlay come
/// back on the controller's thread. Calls are queued in order per overlay, so e.g. an EndSession
/// issued while a build is still running takes effect right after that build.
/// </summary>
internal sealed class OverlayHost
{
    private readonly ISwitcherView _window;
    private readonly UiThread _thread;

    public MonitorInfo Monitor { get; }
    public IntPtr Handle { get; }

    /// <summary>Raised on the controller's thread.</summary>
    public event Action<int>? TileClicked;
    /// <summary>Raised on the controller's thread.</summary>
    public event Action? Deactivated;
    /// <summary>Raised on the controller's thread.</summary>
    public event Action<int>? TileCloseRequested;
    /// <summary>Raised on the controller's thread (hoverSelects).</summary>
    public event Action<int>? TileHovered;
    /// <summary>Raised on the controller's thread: a stack's badge was clicked.</summary>
    public event Action<int>? TileBadgeClicked;
    public event Action<int>? TileMoveHereClicked;
    public event Action<int, TileMenuCommand>? TileMenuChosen;

    public OverlayHost(MonitorInfo monitor, UiThread controller)
    {
        Monitor = monitor;
        ISwitcherView? window = null;
        UiThread? queue = null;
        Exception? error = null;

        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                queue = UiThread.Current;
                window = new SwitcherWindow(monitor);
                window.TileClicked += index => controller.Post(() => TileClicked?.Invoke(index));
                window.SwitcherDeactivated += () => controller.Post(() => Deactivated?.Invoke());
                window.TileCloseRequested += index => controller.Post(() => TileCloseRequested?.Invoke(index));
                window.TileHovered += index => controller.Post(() => TileHovered?.Invoke(index));
                window.TileBadgeClicked += index => controller.Post(() => TileBadgeClicked?.Invoke(index));
                window.TileMoveHereClicked += index => controller.Post(() => TileMoveHereClicked?.Invoke(index));
                window.TileMenuChosen += (index, command) => controller.Post(() => TileMenuChosen?.Invoke(index, command));
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                ready.Set();
            }
            if (error is null) UiThread.Run();
        })
        {
            IsBackground = true,
            Name = $"Overlay {monitor.DeviceName}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (error is not null) throw new InvalidOperationException($"Overlay thread for {monitor.DeviceName} failed", error);
        _window = window!;
        _thread = queue!;
        Handle = _window.Handle;
    }

    /// <summary>Pure layout math on the monitor's size; safe from any thread.</summary>
    public int MaxColumns(int count, Settings settings) => _window.MaxColumns(count, settings);

    /// <summary>Whole rows visible at once (Page Up / Page Down); pure layout math, safe from any thread.</summary>
    public int RowsPerPage(int count, Settings settings, int columns) => _window.RowsPerPage(count, settings, columns);

    /// <summary>Builds the switcher cloaked; completes once its first frame has been drawn.</summary>
    public Task PrepareAsync(IReadOnlyList<SwitcherItem> items, int selected, Settings settings, int columns, string query) =>
        _thread.InvokeAsync(() =>
        {
            _window.SetCloaked(true);
            _window.ShowSession(items, selected, settings, columns, query); // paints before returning
        });

    /// <summary>Rebuilds the tiles of a visible switcher in place (e.g. after a window was closed).</summary>
    public void Update(IReadOnlyList<SwitcherItem> items, int selected, Settings settings, int columns, string query) =>
        _thread.InvokeAsync(() => _window.ShowSession(items, selected, settings, columns, query));

    /// <summary>Hides this monitor's switcher (e.g. its last window was closed) without ending the session.</summary>
    public void Hide() => _thread.InvokeAsync(() =>
    {
        if (_window.IsVisible) _window.EndSession();
    });

    /// <summary>Uncloaks the (already drawn) switcher. A DWM call on the HWND: no thread hop needed.</summary>
    public void Reveal() => _window.SetCloaked(false);

    public void Select(int index) => _thread.InvokeAsync(() => _window.SetSelected(index));

    public void EndSession() => _thread.InvokeAsync(() =>
    {
        if (_window.IsVisible) _window.EndSession();
    });

    /// <summary>
    /// Synchronously reads overlay state on its thread (diagnostics and self-test only), after every
    /// update already queued.
    /// </summary>
    public T Query<T>(Func<ISwitcherView, T> read) => _thread.Invoke(() => read(_window));

    public void Close()
    {
        try
        {
            _thread.Invoke(() =>
            {
                _window.Close();
                return 0;
            });
        }
        finally
        {
            _thread.Shutdown();
        }
    }
}
