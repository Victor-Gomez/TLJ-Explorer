using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class RawFormatTests
{
    [Fact]
    public void ReadAll_ReturnsAllBytesFromStream()
    {
        byte[] payload = [10, 20, 30, 40, 50];
        using var stream = new MemoryStream(payload);

        Assert.Equal(payload, RawFormat.ReadAll(stream));
    }

    [Fact]
    public void ReadAll_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RawFormat.ReadAll(null!));
    }

    [Fact]
    public void ToHexDump_NullData_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RawFormat.ToHexDump(null!));
    }

    [Fact]
    public void ToHexDump_Empty_ReturnsEmptyString()
    {
        Assert.Equal("", RawFormat.ToHexDump([]));
    }

    [Fact]
    public void ToHexDump_SingleLine_FormatsOffsetHexAndAscii()
    {
        // "Hi!" (33..127 range) followed by a NUL, which prints as '.'.
        byte[] data = [(byte)'H', (byte)'i', (byte)'!', 0x00];

        string dump = RawFormat.ToHexDump(data);

        Assert.StartsWith("0000:0000 | 48 69 21 00", dump, StringComparison.Ordinal);
        Assert.Contains("| Hi!.", dump, StringComparison.Ordinal);
        Assert.EndsWith("\n", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHexDump_PartialLastLine_PadsMissingBytesWithSpaces()
    {
        byte[] data = [0xAB, 0xCD];

        string dump = RawFormat.ToHexDump(data);

        // Two real hex bytes, then 14 blank two-space slots, then the ASCII column (both bytes
        // are outside the printable 33..127 range, so they render as dots).
        string expectedHexColumn = "AB CD " + string.Concat(Enumerable.Repeat("   ", 14));
        Assert.Contains(expectedHexColumn + "| ..", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHexDump_MultipleLines_IncrementsOffset()
    {
        byte[] data = new byte[20];

        string dump = RawFormat.ToHexDump(data);
        string[] lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("0000:0000", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("0000:0010", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ToHexDump_LargeInput_TruncatesAt128KiB()
    {
        byte[] data = new byte[200 * 1024];

        string dump = RawFormat.ToHexDump(data);
        string[] lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal((128 * 1024) / 16, lines.Length);
    }

    [Fact]
    public void ToHexDump_NonPrintableBytes_RenderAsDot()
    {
        byte[] data = [0x00, 32, 128, 255];

        string dump = RawFormat.ToHexDump(data);

        Assert.Contains("| ....", dump, StringComparison.Ordinal);
    }
}
