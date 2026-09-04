using System.IO;

namespace TLJExplorer.Services;

/// <summary>
/// The application's single append-only log. Writes UTF-8 lines to <c>log.txt</c> under the user's local
/// application-data folder, prefixed with a timestamp, level and thread id. Safe to call from any thread;
/// failures during logging are swallowed so a broken log file never takes the app down.
/// </summary>
/// <remarks>
/// There used to be two of these -- this one writing to the OS temp folder and a separate <c>Logger</c>
/// writing here -- and the crash handlers used the other one. That meant unhandled exceptions, the single
/// most important thing in a log, were written to a file that Help &gt; Open Log File and the About box's
/// diagnostics row never pointed at. They are now one class writing one file.
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>
    /// Rotate at 5 MB. The log lives in a folder nothing ever cleans (unlike the temp directory it used
    /// to sit in), so without a cap a long-lived install would grow one unboundedly.
    /// </summary>
    private const long MaxSizeBytes = 5 * 1024 * 1024;

    /// <summary>Absolute path to the current log file, for surfacing to the user (Help &gt; Open Log File).</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TLJExplorer", "log.txt");

    /// <summary>The previous log, kept across one rotation so a crash isn't lost to the next session's writes.</summary>
    public static string PreviousFilePath { get; } = Path.ChangeExtension(FilePath, ".previous.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERR ", message);

    public static void Exception(string context, Exception ex) =>
        Write("ERR ", $"{context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RotateIfOversized();

                // Thread id matters for the crash handlers in particular: an unobserved task exception and
                // a dispatcher exception look alike in the text otherwise.
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId,3}] {message}{Environment.NewLine}";
                File.AppendAllText(FilePath, line);
            }
        }
        catch
        {
            // Logging must never itself become a new source of crashes.
        }
    }

    /// <summary>Caller holds <see cref="Gate"/>. Never throws out; rotation failing must not lose the write.</summary>
    private static void RotateIfOversized()
    {
        try
        {
            var current = new FileInfo(FilePath);
            if (!current.Exists || current.Length < MaxSizeBytes)
                return;

            File.Move(FilePath, PreviousFilePath, overwrite: true);
        }
        catch (Exception)
        {
            // A locked or unmovable log is not worth failing the write for -- carry on appending.
        }
    }
}
