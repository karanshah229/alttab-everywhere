using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AltTabEverywhere;

/// <summary>
/// Gives memory back to Windows once the switcher has been closed for a moment: compacts the .NET
/// heap and trims the working set. One-shot after each session, never a repeating timer, so idle
/// CPU stays at zero. Trimmed pages are paged back in on the next Alt+Tab (a few ms).
/// </summary>
internal sealed class MemoryTrimmer
{
    private readonly DispatcherTimer _timer;

    public MemoryTrimmer(TimeSpan delay)
    {
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            TrimNow();
        };
    }

    /// <summary>(Re)starts the countdown. Call when a session ends.</summary>
    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Call when a session starts, so we never trim while the switcher is in use.</summary>
    public void Cancel() => _timer.Stop();

    public static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception ex)
        {
            Log.Error("trim memory", ex);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
