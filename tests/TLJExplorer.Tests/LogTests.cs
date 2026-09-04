using System.IO;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// The app used to have two loggers writing to two files, and the crash handlers used the one that
/// Help &gt; Open Log File never opened. There is now a single <see cref="Log"/>; these pin where it
/// writes, since that path is what the menu item and the About box's diagnostics row both surface.
/// </summary>
public class LogTests
{
    [Fact]
    public void TheLogLivesUnderLocalApplicationData()
    {
        // Deliberately not the temp folder: crash logs have to survive a temp clean to be worth reading,
        // and this puts the log next to the app's other per-user state.
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TLJExplorer", "log.txt");

        Assert.Equal(expected, Log.FilePath);
    }

    [Fact]
    public void TheRotatedLogSitsBesideTheCurrentOne()
    {
        Assert.Equal(Path.GetDirectoryName(Log.FilePath), Path.GetDirectoryName(Log.PreviousFilePath));
        Assert.NotEqual(Log.FilePath, Log.PreviousFilePath);
        Assert.EndsWith(".previous.txt", Log.PreviousFilePath);
    }
}
