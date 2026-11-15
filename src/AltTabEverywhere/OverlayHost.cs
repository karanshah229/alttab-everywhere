using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AltTabEverywhere;

/// <summary>
/// Runs one monitor's <see cref="SwitcherWindow"/> on its own UI thread, so all monitors build
/// and draw their switchers in parallel instead of one after another.
///
/// Every call from the controller is marshalled onto the overlay's thread; events from the overlay
/// come back on the controller's thread. Calls are queued in order per overlay, so e.g. an
/// EndSession issued while a build is still running takes effect right after that build.
/// </summary>
internal sealed class OverlayHost
{
    private readonly SwitcherWindow _window;
    private readonly Dispatcher _dispatcher;

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

    public OverlayHost(MonitorInfo monitor, Dispatcher controller)
    {
        Monitor = monitor;
        SwitcherWindow? window = null;
        Dispatcher? dispatcher = null;
        Exception? error = null;

        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                window = new SwitcherWindow(monitor);
                window.TileClicked += index => controller.InvokeAsync(() => TileClicked?.Invoke(index));
                window.Deactivated += (_, _) => controller.InvokeAsync(() => Deactivated?.Invoke());
                window.TileCloseRequested += index => controller.InvokeAsync(() => TileCloseRequested?.Invoke(index));
                window.TileHovered += index => controller.InvokeAsync(() => TileHovered?.Invoke(index));
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                ready.Set();
            }
            if (error is null) Dispatcher.Run();
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
        _dispatcher = dispatcher!;
        Handle = _window.Handle;
    }

    /// <summary>Pure layout math on the monitor's size; safe from any thread.</summary>
    public int MaxColumns(int count, Settings settings) => _window.MaxColumns(count, settings);

    /// <summary>Whole rows visible at once (Page Up / Page Down); pure layout math, safe from any thread.</summary>
    public int RowsPerPage(int count, Settings settings, int columns) => _window.RowsPerPage(count, settings, columns);

    /// <summary>Builds the switcher cloaked; completes once its first frame has been drawn.</summary>
    public Task PrepareAsync(IReadOnlyList<SwitcherItem> items, int selected, Settings settings, int columns, string query) =>
        _dispatcher.InvokeAsync(async () =>
        {
            _window.SetCloaked(true);
            _window.ShowSession(items, selected, settings, columns, query);
            await Dispatcher.Yield(DispatcherPriority.Background); // runs after the Render pass
        }).Task.Unwrap();

    /// <summary>Rebuilds the tiles of a visible switcher in place (e.g. after a window was closed).</summary>
    public void Update(IReadOnlyList<SwitcherItem> items, int selected, Settings settings, int columns, string query) =>
        _dispatcher.InvokeAsync(() => _window.ShowSession(items, selected, settings, columns, query));

    /// <summary>Hides this monitor's switcher (e.g. its last window was closed) without ending the session.</summary>
    public void Hide() => _dispatcher.InvokeAsync(() =>
    {
        if (_window.IsVisible) _window.EndSession();
    });

    /// <summary>Uncloaks the (already drawn) switcher. A DWM call on the HWND: no thread hop needed.</summary>
    public void Reveal() => _window.SetCloaked(false);

    public void Select(int index) => _dispatcher.InvokeAsync(() => _window.SetSelected(index));

    public void Refresh(int index, SwitcherItem item) => _dispatcher.InvokeAsync(() => _window.RefreshTile(index, item));

    public void EndSession() => _dispatcher.InvokeAsync(() =>
    {
        if (_window.IsVisible) _window.EndSession();
    });

    /// <summary>
    /// Synchronously reads overlay state on its thread (diagnostics and self-test only). Runs at
    /// Normal priority, i.e. after every update already queued (Invoke's default, Send, would jump
    /// ahead of them and read stale state).
    /// </summary>
    public T Query<T>(Func<SwitcherWindow, T> read) => _dispatcher.Invoke(() => read(_window), DispatcherPriority.Normal);

    public void Close()
    {
        try
        {
            _dispatcher.Invoke(() => _window.Close());
        }
        finally
        {
            _dispatcher.InvokeShutdown();
        }
    }
}
