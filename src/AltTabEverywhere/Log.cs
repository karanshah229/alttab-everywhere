using System;
using System.IO;

namespace AltTabEverywhere;

/// <summary>Tiny append-only log in %LOCALAPPDATA%\AltTabEverywhere\log.txt. Never logs window titles.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltTabEverywhere");

    public static string FilePath { get; } = Path.Combine(Directory, "log.txt");

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
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
