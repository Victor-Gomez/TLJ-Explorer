using System.IO;

namespace TLJExplorer.Services;

/// <summary>
/// Minimal append-only file logger. Writes UTF-8 lines to <c>%TEMP%\TLJExplorer.log</c> (or the platform
/// temp equivalent) prefixed with a timestamp. Safe to call from any thread; failures during logging are
/// swallowed so a broken log file never takes the app down. Zero setup: first call creates the file.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TLJExplorer.log");

    /// <summary>Absolute path to the current log file, for surfacing to the user (e.g. Help > Open log).</summary>
    public static string FilePath => Path;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERR ", message);

    public static void Exception(string context, Exception ex) =>
        Write("ERR ", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(Path, line);
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
