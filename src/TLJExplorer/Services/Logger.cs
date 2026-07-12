using System.IO;

namespace TLJExplorer.Services;

/// <summary>Minimal append-to-disk logger. Unbuffered so log lines survive process crashes.</summary>
public static class Logger
{
    public static readonly string LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TLJExplorer", "log.txt");
    private static readonly object Gate = new();

    static Logger()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
    }

    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogFilePath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never itself become a new source of crashes.
            }
        }
    }
}
