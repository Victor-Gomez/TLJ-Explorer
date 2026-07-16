using System.IO;

namespace TLJExplorer.Services;

/// <summary>
/// Owns a single fixed scratch directory (<c>TLJExplorer/</c> under the OS temp folder) that every extracted sound/video/PNG
/// frame is written into. The directory is wiped wholesale on startup (sweeping up after any prior crash)
/// and again on exit. No per-item tracking -- callers just ask for a unique path inside the scratch dir.
/// </summary>
public sealed class TempFileTracker
{
    public string ScratchDirectory { get; } = Path.Combine(Path.GetTempPath(), "TLJExplorer");

    public TempFileTracker()
    {
        // Sweep any leftovers from a prior crashed session, then re-create empty.
        Cleanup();
        Directory.CreateDirectory(ScratchDirectory);
    }

    /// <summary>Returns a fresh, unique file path inside the scratch dir with the given extension (leading dot).</summary>
    public string CreateTempFile(string extension)
    {
        return Path.Combine(ScratchDirectory, $"{Guid.NewGuid():N}{extension}");
    }

    /// <summary>Creates and returns a fresh, unique subdirectory inside the scratch dir.</summary>
    public string CreateTempDirectory()
    {
        string path = Path.Combine(ScratchDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes the entire scratch directory and everything in it. Best-effort; individual failures are swallowed.</summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(ScratchDirectory))
                Directory.Delete(ScratchDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file might still be locked (e.g. LibVLC holding a WAV open). The OS temp folder
            // gets swept eventually, and startup will retry.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
