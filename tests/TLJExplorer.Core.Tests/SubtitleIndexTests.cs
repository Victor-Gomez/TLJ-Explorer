using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class SubtitleIndexTests
{
    [Fact]
    public void WriteCsv_EscapesQuotesAndCommas()
    {
        var entries = new[]
        {
            new SubtitleEntry(@"\a\b\c.isn", "april", "Hello, world!", "room01"),
            new SubtitleEntry(@"\a\b\d.isn", "brian", "She said \"no\"", "room01"),
        };

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            SubtitleIndex.WriteCsv(entries, path);
            string content = File.ReadAllText(path);

            Assert.Contains("path,scene,speaker,text", content, StringComparison.Ordinal);
            Assert.Contains("\"Hello, world!\"", content, StringComparison.Ordinal);
            Assert.Contains("\"She said \"\"no\"\"\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void WriteSrtPerScene_EmitsOneFilePerScene_WithSequentialTimings()
    {
        var entries = new[]
        {
            new SubtitleEntry(@"\room01\a.isn", "april", "Line 1", "room01"),
            new SubtitleEntry(@"\room01\b.isn", "brian", "Line 2", "room01"),
            new SubtitleEntry(@"\room02\c.isn", "april", "Line 3", "room02"),
        };

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            int count = SubtitleIndex.WriteSrtPerScene(entries, dir);

            Assert.Equal(2, count);
            string room01 = File.ReadAllText(Path.Combine(dir, "room01.srt"));
            string room02 = File.ReadAllText(Path.Combine(dir, "room02.srt"));

            Assert.Contains("Line 1", room01, StringComparison.Ordinal);
            Assert.Contains("Line 2", room01, StringComparison.Ordinal);
            Assert.Contains("Line 3", room02, StringComparison.Ordinal);
            // Second cue should have a later timestamp than the first.
            Assert.Contains("00:00:00,000 --> 00:00:03,000", room01, StringComparison.Ordinal);
            Assert.Contains("00:00:03,500 --> 00:00:06,500", room01, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
