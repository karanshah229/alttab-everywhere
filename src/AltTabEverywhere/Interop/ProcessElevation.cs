using System;
using System.Runtime.InteropServices;

namespace AltTabEverywhere.Interop;

/// <summary>Whether a process runs as administrator (elevated), for the tiles' admin badge.</summary>
internal static class ProcessElevation
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int ERROR_ACCESS_DENIED = 5;

    private static readonly Lazy<bool> SelfElevated = new(() => IsElevated(Native.GetCurrentProcess()));

    /// <summary>
    /// True if the process is elevated. A process we may open but whose token we may not read is
    /// treated as elevated when we are not: that is what a higher integrity level looks like from below.
    /// </summary>
    public static bool IsElevated(uint processId)
    {
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return false; // protected or gone: no badge
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token))
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED && !SelfElevated.Value;
            try
            {
                return ReadElevation(token);
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>Whether this app itself runs as administrator.</summary>
    public static bool IsSelfElevated => SelfElevated.Value;

    private const int TokenUIAccess = 26;
    private static readonly Lazy<bool> SelfUiAccess = new(() =>
    {
        if (!OpenProcessToken(Native.GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            return GetTokenInformation(token, TokenUIAccess, out int value, sizeof(int), out _) && value != 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    });

    /// <summary>
    /// Whether this app runs with uiAccess (the signed build install.ps1 -AdminWindows puts under
    /// Program Files): it then gets keys while an administrator window has focus and may act on such windows.
    /// </summary>
    public static bool HasUiAccess => SelfUiAccess.Value;

    private static bool IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            return ReadElevation(token);
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static bool ReadElevation(IntPtr token) =>
        GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
