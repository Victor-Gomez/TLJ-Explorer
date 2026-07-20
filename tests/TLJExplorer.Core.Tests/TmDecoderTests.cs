using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class TmDecoderTests
{
    private const uint ImageBlockType = 0x02faf080;
    private const uint PaletteBlockType = 0x02faf082;

    /// <summary>Writes a BIFF file header: "BIFF" id, version, two unknown uint32s, and a block count.</summary>
    private static void WriteFileHeader(BinaryWriter w, uint version, uint numBlocks)
    {
        w.Write("BIFF"u8);
        w.Write(version);
        w.Write(0u); // Unknown1
        w.Write(0u); // Unknown2
        w.Write(numBlocks);
    }

    /// <summary>Writes one leaf BIFF block (header + payload + a zero-sub-block trailer).</summary>
    private static void WriteBlock(BinaryWriter w, uint version, uint typeId, byte[] payload)
    {
        w.Write(0u); // BeginMarker
        w.Write(typeId);
        w.Write(0u); // Unknown1
        w.Write(payload.Length); // DataSize
        if (version == 2)
            w.Write(0u); // Unknown2

        w.Write(payload);

        w.Write(0u); // EndMarker
        w.Write(0u); // NumSubBlocks
    }

    private static byte[] BuildPalettePayload(params (byte R, byte G, byte B)[] entries)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((uint)entries.Length);
        foreach (var (r, g, b) in entries)
        {
            w.Write((ushort)r);
            w.Write((ushort)g);
            w.Write((ushort)b);
        }

        return stream.ToArray();
    }

    private static byte[] BuildImagePayload(string name, int width, int height, byte[][] levels)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);

        byte[] nameBytes = Encoding.ASCII.GetBytes(name + "\0");
        w.Write((ushort)nameBytes.Length);
        w.Write(nameBytes);
        w.Write((byte)0); // Unknown1
        w.Write(width);
        w.Write(height);
        w.Write((uint)levels.Length);
        foreach (byte[] level in levels)
            w.Write(level);

        return stream.ToArray();
    }

    [Fact]
    public void Decode_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TmDecoder.Decode(null!));
    }

    [Fact]
    public void Decode_WrongId_ThrowsFormatException()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write("XXXX"u8);
        stream.Position = 0;

        FormatException ex = Assert.Throws<FormatException>(() => TmDecoder.Decode(stream));
        Assert.Contains("BIFF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_UnsupportedVersion_ThrowsFormatException()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 3, numBlocks: 0);
        stream.Position = 0;

        Assert.Throws<FormatException>(() => TmDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_NoBlocks_ReturnsEmptyList()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 1, numBlocks: 0);
        stream.Position = 0;

        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream);

        Assert.Empty(entries);
    }

    [Fact]
    public void Decode_ImageWithPrecedingPalette_ResolvesPaletteColors()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 1, numBlocks: 2);

        byte[] palettePayload = BuildPalettePayload((10, 20, 30), (40, 50, 60));
        WriteBlock(w, version: 1, PaletteBlockType, palettePayload);

        // A 2x1 single-level image: index 0 then index 1.
        byte[] imagePayload = BuildImagePayload("tex1", 2, 1, [[0, 1]]);
        WriteBlock(w, version: 1, ImageBlockType, imagePayload);

        stream.Position = 0;
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream);

        Assert.Single(entries);
        TmEntry entry = entries[0];
        Assert.Equal("tex1", entry.Name);
        Assert.Equal(2, entry.Image.Width);
        Assert.Equal(1, entry.Image.Height);

        // Pixel 0 -> palette[0] = (10,20,30), pixel 1 -> palette[1] = (40,50,60), BGRA order.
        Assert.Equal([30, 20, 10, 255], entry.Image.Pixels[0..4]);
        Assert.Equal([60, 50, 40, 255], entry.Image.Pixels[4..8]);
    }

    [Fact]
    public void Decode_ImageWithoutPalette_FallsBackToBlack()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 1, numBlocks: 1);

        byte[] imagePayload = BuildImagePayload("nopalette", 1, 1, [[5]]);
        WriteBlock(w, version: 1, ImageBlockType, imagePayload);

        stream.Position = 0;
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream);

        Assert.Equal([0, 0, 0, 255], entries[0].Image.Pixels);
    }

    [Fact]
    public void Decode_UnknownBlockType_IsSkippedButDoesNotThrow()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 1, numBlocks: 1);
        WriteBlock(w, version: 1, typeId: 0xDEADBEEF, payload: [1, 2, 3, 4, 5]);

        stream.Position = 0;
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream);

        Assert.Empty(entries);
    }

    [Fact]
    public void Decode_Version2_ReadsExtraUnknownFieldPerBlock()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 2, numBlocks: 1);
        byte[] imagePayload = BuildImagePayload("v2tex", 1, 1, [[0]]);
        WriteBlock(w, version: 2, ImageBlockType, imagePayload);

        stream.Position = 0;
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream, useMipMap: false);

        Assert.Single(entries);
        Assert.Equal("v2tex", entries[0].Name);
    }

    [Fact]
    public void Decode_MultiLevelImage_UseMipMapFalse_OnlyDecodesBaseLevel()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 1, numBlocks: 1);

        byte[] level0 = [0, 0, 0, 0]; // 2x2 base level
        byte[] level1 = [0];          // 1x1 mip level
        byte[] imagePayload = BuildImagePayload("mip", 2, 2, [level0, level1]);
        WriteBlock(w, version: 1, ImageBlockType, imagePayload);

        stream.Position = 0;
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream, useMipMap: false);

        // Base-level-only decode: image dimensions equal the declared Width/Height exactly.
        Assert.Equal(2, entries[0].Image.Width);
        Assert.Equal(2, entries[0].Image.Height);
    }

    [Fact]
    public void Decode_MultiLevelImage_UseMipMapTrue_ProducesWiderCanvasForMipStrip()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        WriteFileHeader(w, version: 1, numBlocks: 1);

        byte[] level0 = [0, 0, 0, 0]; // 2x2
        byte[] level1 = [0];          // 1x1
        byte[] imagePayload = BuildImagePayload("mip", 2, 2, [level0, level1]);
        WriteBlock(w, version: 1, ImageBlockType, imagePayload);

        stream.Position = 0;
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream, useMipMap: true);

        // canvasWidth = width + width/2 = 2 + 1 = 3; height unchanged.
        Assert.Equal(3, entries[0].Image.Width);
        Assert.Equal(2, entries[0].Image.Height);
    }
}
