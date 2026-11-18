using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static AltTabEverywhere.Interop.Win32;

namespace AltTabEverywhere;

/// <summary>
/// A Win32 message loop and work queue for one thread, in place of WPF's Dispatcher: work is posted to
/// a message-only window, timers are Windows timers on it (see <see cref="UiTimer"/>), and `await`
/// comes back to the thread through its <see cref="SynchronizationContext"/>. Between messages the
/// thread sleeps in GetMessage: no polling.
/// </summary>
internal sealed class UiThread
{
    private const uint WM_RUN = 0x8001; // WM_APP + 1
    private const uint WM_TIMER = 0x0113;
    private const uint PM_NOREMOVE = 0;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [ThreadStatic] private static UiThread? _current;

    /// <summary>This thread's queue (created on first use).</summary>
    public static UiThread Current => _current ??= new UiThread();

    private readonly IntPtr _hwnd;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly object _lock = new();
    private readonly Queue<Action> _queue = new();
    private readonly Queue<Action> _idle = new();
    private bool _wakePosted;
    private readonly Dictionary<nuint, UiTimer> _timers = new();
    private nuint _nextTimerId;
    private GCHandle _self;

    private unsafe UiThread()
    {
        EnsureClass();
        fixed (char* cls = ClassName)
            _hwnd = CreateWindowExW(0, cls, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"message window failed ({Marshal.GetLastPInvokeError()})");
        _self = GCHandle.Alloc(this);
        SetWindowLongPtrW(_hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(_self));
        SynchronizationContext.SetSynchronizationContext(new Context(this));
    }

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Queues <paramref name="action"/> to run on this thread, after what is already queued.</summary>
    public void Post(Action action)
    {
        lock (_lock)
        {
            _queue.Enqueue(action);
            if (_wakePosted) return;
            _wakePosted = true;
        }
        PostMessageW(_hwnd, WM_RUN, 0, 0);
    }

    /// <summary>Queues <paramref name="action"/> to run once nothing else is waiting (input, painting, other work).</summary>
    public void PostIdle(Action action)
    {
        lock (_lock) _idle.Enqueue(action);
        Post(static () => { }); // wake the loop so it notices
    }

    /// <summary>Completes once the thread has nothing else to do.</summary>
    public Task YieldIdle()
    {
        var done = new TaskCompletionSource();
        PostIdle(() => done.SetResult());
        return done.Task;
    }

    public Task InvokeAsync(Action action)
    {
        var done = new TaskCompletionSource();
        Post(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public Task InvokeAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource();
        Post(async () =>
        {
            try { await work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public T Invoke<T>(Func<T> read)
    {
        if (IsCurrentThread) return read();
        var done = new TaskCompletionSource<T>();
        Post(() =>
        {
            try { done.SetResult(read()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Ends this thread's <see cref="Run"/> with <paramref name="exitCode"/>.</summary>
    public void Shutdown(int exitCode) => Post(() => PostQuitMessage(exitCode));

    public void Shutdown() => Shutdown(0);

    /// <summary>The message loop: returns the exit code once <see cref="Shutdown(int)"/> is called.</summary>
    public static unsafe int Run()
    {
        var thread = Current;
        MSG msg;
        while (true)
        {
            if (thread.HasIdleWork && PeekMessageW(&msg, IntPtr.Zero, 0, 0, PM_NOREMOVE) == 0)
            {
                thread.RunIdleItem();
                continue;
            }
            int result = GetMessageW(&msg, IntPtr.Zero, 0, 0);
            if (result == 0) break;
            if (result == -1) return -1;
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        thread.Destroy();
        return (int)msg.wParam;
    }

    private bool HasIdleWork
    {
        get { lock (_lock) return _idle.Count > 0; }
    }

    private void RunIdleItem()
    {
        Action? action;
        lock (_lock) action = _idle.Count > 0 ? _idle.Dequeue() : null;
        if (action is not null) Execute(action);
    }

    private void RunQueue()
    {
        while (true)
        {
            Action action;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _wakePosted = false;
                    return;
                }
                action = _queue.Dequeue();
            }
            Execute(action);
        }
    }

    /// <summary>Unhandled exceptions are logged, never fatal: an always-on app keeps running.</summary>
    private static void Execute(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("unhandled", ex); }
    }

    private void Destroy()
    {
        foreach (var id in _timers.Keys) KillTimer(_hwnd, id);
        _timers.Clear();
        DestroyWindow(_hwnd);
        if (_self.IsAllocated) _self.Free();
        if (_current == this) _current = null;
    }

    // ---- Timers ----

    internal nuint StartTimer(UiTimer timer, nuint id, uint milliseconds)
    {
        if (id == 0) id = ++_nextTimerId;
        _timers[id] = timer;
        SetTimer(_hwnd, id, Math.Max(1u, milliseconds), IntPtr.Zero); // same id: restarts the countdown
        return id;
    }

    internal void StopTimer(nuint id)
    {
        if (id == 0) return;
        KillTimer(_hwnd, id);
        _timers.Remove(id);
    }

    private IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_RUN:
                RunQueue();
                return IntPtr.Zero;
            case WM_TIMER:
                if (_timers.TryGetValue((nuint)wParam, out var timer)) Execute(timer.Fire);
                return IntPtr.Zero;
        }
        return DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

    // ---- Window class ----

    private const string ClassName = "AltTabEverywhere.Thread";
    private static readonly object ClassLock = new();
    private static bool _registered;

    private static unsafe void EnsureClass()
    {
        lock (ClassLock)
        {
            if (_registered) return;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &StaticWndProc, hInstance = GetModuleHandleW(null), lpszClassName = name };
                if (RegisterClassExW(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
            }
            _registered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr self = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (self == IntPtr.Zero) return DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            return ((UiThread)GCHandle.FromIntPtr(self).Target!).WndProc(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Error($"thread message 0x{msg:X}", ex);
            return IntPtr.Zero;
        }
    }

    private sealed class Context(UiThread thread) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => thread.Post(() => callback(state));
        public override void Send(SendOrPostCallback callback, object? state) => thread.Invoke(() => { callback(state); return 0; });
        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>
/// A timer on a <see cref="UiThread"/>, in place of WPF's DispatcherTimer (same members). A Windows
/// timer exists only while it's started: a stopped timer costs nothing.
/// </summary>
internal sealed class UiTimer
{
    private readonly UiThread _thread = UiThread.Current;
    private nuint _id;
    private TimeSpan _interval = TimeSpan.FromMilliseconds(100);

    public event EventHandler? Tick;

    public bool IsEnabled { get; private set; }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value;
            if (IsEnabled) Start(); // as DispatcherTimer: a new interval restarts the countdown
        }
    }

    public void Start()
    {
        _id = _thread.StartTimer(this, _id, (uint)Math.Clamp(_interval.TotalMilliseconds, 1, int.MaxValue));
        IsEnabled = true;
    }

    public void Stop()
    {
        _thread.StopTimer(_id);
        _id = 0;
        IsEnabled = false;
    }

    internal void Fire() => Tick?.Invoke(this, EventArgs.Empty);
}
