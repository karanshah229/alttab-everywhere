using System;
using System.IO;

namespace AltTabEverywhere;

/// <summary>
/// Tiny append-only log in %LOCALAPPDATA%\AltTabEverywhere\log.txt. Never logs window titles.
/// Rotates at 1 MB to log.1.txt (one old file kept), so it can't grow forever.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltTabEverywhere");

    public static string FilePath { get; } = Path.Combine(Directory, "log.txt");

    public static string OldFilePath { get; } = Path.Combine(Directory, "log.1.txt");

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, OldFilePath, overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the switcher down.
        }
    }

    public static void Error(string context, Exception ex) => Info($"ERROR {context}: {ex}");
}
