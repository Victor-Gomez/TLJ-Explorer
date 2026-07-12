using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>One named sub-image decoded out of a TM texture file.</summary>
public sealed record TmEntry(string Name, DecodedImage Image);

/// <summary>
/// Decoder for the TM texture format: palette-indexed textures, with
/// optional mip levels, stored inside a generic recursive "BIFF" block
/// container.
/// </summary>
/// <remarks>
/// <para>File header (little-endian):</para>
/// <code>
/// char[4] Id           must equal ASCII "BIFF"
/// UInt32  Version       1 or 2
/// UInt32  Unknown1
/// UInt32  Unknown2
/// UInt32  NumBlocks
/// </code>
/// <para>
/// Followed by <c>NumBlocks</c> top-level blocks, each recursively shaped as:
/// </para>
/// <code>
/// BlockHeader:
///   UInt32 BeginMarker
///   UInt32 TypeId
///   UInt32 Unknown1
///   Int32  DataSize
///   UInt32 Unknown2        (only present when file Version == 2)
/// &lt;DataSize bytes of payload, interpreted per TypeId&gt;
/// BlockTrailer:
///   UInt32 EndMarker
///   UInt32 NumSubBlocks
/// &lt;NumSubBlocks nested Blocks, recursively&gt;
/// </code>
/// <para>
/// A "current palette" is threaded by reference through the entire
/// recursive block walk: once a Palette block (TypeId 0x02faf082) is seen,
/// all subsequently processed Image blocks (including nested sub-blocks and
/// later siblings) use it, until replaced by another Palette block.
/// </para>
/// <para>
/// Image blocks (TypeId 0x02faf080) payload:
/// </para>
/// <code>
/// UInt16 NameLen         (includes trailing NUL terminator in the count)
/// char[NameLen] Name     (actual string is the first NameLen-1 chars)
/// Byte   Unknown1
/// Int32  Width
/// Int32  Height
/// UInt32 NumLevels
/// &lt;for each of NumLevels mip levels (level 0 = Width x Height, each
///   subsequent level halves both dimensions via integer division, minimum
///   1x1): levelWidth*levelHeight bytes, each a palette index&gt;
/// </code>
/// <para>
/// Palette blocks (TypeId 0x02faf082) payload:
/// </para>
/// <code>
/// UInt32 NumEntries
/// repeat NumEntries: { UInt16 R, UInt16 G, UInt16 B }
/// </code>
/// <para>
/// Each channel occupies a 16-bit field, but the value itself is a plain 0-255 byte (the low byte) --
/// it is not a real 16-bit color depth needing a right-shift.
/// </para>
/// <para>
/// Other TypeIds are opaque: their DataSize bytes are skipped without
/// interpretation, but their sub-blocks are still walked (and palette state
/// still threads through them).
/// </para>
/// </remarks>
public static class TmDecoder
{
    private const uint ImageBlockType = 0x02faf080;
    private const uint PaletteBlockType = 0x02faf082;

    public static IReadOnlyList<TmEntry> Decode(Stream stream, bool useMipMap = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var id = reader.ReadBytes(4);
        if (id.Length != 4 || Encoding.ASCII.GetString(id) != "BIFF")
        {
            throw new FormatException("TM file does not start with the expected 'BIFF' identifier.");
        }

        uint version = reader.ReadUInt32();
        if (version is not (1 or 2))
        {
            throw new FormatException($"Unsupported TM/BIFF version {version}; expected 1 or 2.");
        }

        _ = reader.ReadUInt32(); // Unknown1
        _ = reader.ReadUInt32(); // Unknown2
        uint numBlocks = reader.ReadUInt32();

        var entries = new List<TmEntry>();
        (byte R, byte G, byte B)[]? currentPalette = null;

        for (uint i = 0; i < numBlocks; i++)
        {
            ReadBlock(reader, version, entries, ref currentPalette, useMipMap);
        }

        return entries;
    }

    private static void ReadBlock(
        BinaryReader reader,
        uint version,
        List<TmEntry> entries,
        ref (byte R, byte G, byte B)[]? currentPalette,
        bool useMipMap)
    {
        _ = reader.ReadUInt32(); // BeginMarker
        uint typeId = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // Unknown1
        int dataSize = reader.ReadInt32();
        if (version == 2)
        {
            _ = reader.ReadUInt32(); // Unknown2
        }

        long payloadStart = reader.BaseStream.Position;

        if (typeId == ImageBlockType)
        {
            ReadImageBlock(reader, entries, currentPalette, useMipMap);
        }
        else if (typeId == PaletteBlockType)
        {
            currentPalette = ReadPaletteBlock(reader);
        }
        else
        {
            reader.BaseStream.Seek(payloadStart + dataSize, SeekOrigin.Begin);
        }

        // Ensure we land exactly at the end of the declared payload, regardless
        // of how much the handler above actually consumed.
        reader.BaseStream.Seek(payloadStart + dataSize, SeekOrigin.Begin);

        _ = reader.ReadUInt32(); // EndMarker
        uint numSubBlocks = reader.ReadUInt32();

        for (uint i = 0; i < numSubBlocks; i++)
        {
            ReadBlock(reader, version, entries, ref currentPalette, useMipMap);
        }
    }

    private static (byte R, byte G, byte B)[] ReadPaletteBlock(BinaryReader reader)
    {
        uint numEntries = reader.ReadUInt32();
        var palette = new (byte R, byte G, byte B)[numEntries];
        for (uint i = 0; i < numEntries; i++)
        {
            // Each channel is a 16-bit field but only the low byte is populated; use the value directly
            // as a 0-255 byte. Shifting right by 8 would yield black.
            ushort r = reader.ReadUInt16();
            ushort g = reader.ReadUInt16();
            ushort b = reader.ReadUInt16();
            palette[i] = (unchecked((byte)r), unchecked((byte)g), unchecked((byte)b));
        }

        return palette;
    }

    private static void ReadImageBlock(
        BinaryReader reader,
        List<TmEntry> entries,
        (byte R, byte G, byte B)[]? palette,
        bool useMipMap)
    {
        ushort nameLen = reader.ReadUInt16();
        var nameBytes = reader.ReadBytes(nameLen);
        string name = nameLen > 0
            ? Encoding.ASCII.GetString(nameBytes, 0, nameLen - 1)
            : string.Empty;

        _ = reader.ReadByte(); // Unknown1
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        uint numLevels = reader.ReadUInt32();

        var levels = new byte[numLevels][];
        var levelSizes = new (int Width, int Height)[numLevels];

        int levelWidth = width;
        int levelHeight = height;
        for (uint level = 0; level < numLevels; level++)
        {
            levelSizes[level] = (levelWidth, levelHeight);
            int count = checked(levelWidth * levelHeight);
            levels[level] = reader.ReadBytes(count);

            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        DecodedImage image;
        if (!useMipMap || numLevels <= 1)
        {
            var pixels = IndicesToBgra(levels[0], levelSizes[0].Width, levelSizes[0].Height, palette);
            image = new DecodedImage(width, height, pixels) { Name = name };
        }
        else
        {
            int extraColumnWidth = width / 2;
            int canvasWidth = width + extraColumnWidth;
            int canvasHeight = height;
            var canvas = new byte[checked(canvasWidth * canvasHeight * 4)];

            // Opaque black background.
            for (int p = 3; p < canvas.Length; p += 4)
            {
                canvas[p] = 255;
            }

            BlitIndices(canvas, canvasWidth, 0, 0, levels[0], levelSizes[0].Width, levelSizes[0].Height, palette);

            int yOffset = 0;
            for (uint level = 1; level < numLevels; level++)
            {
                var (lw, lh) = levelSizes[level];
                BlitIndices(canvas, canvasWidth, width, yOffset, levels[level], lw, lh, palette);
                yOffset += lh;
            }

            image = new DecodedImage(canvasWidth, canvasHeight, canvas) { Name = name };
        }

        entries.Add(new TmEntry(name, image));
    }

    private static byte[] IndicesToBgra(byte[] indices, int width, int height, (byte R, byte G, byte B)[]? palette)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < indices.Length; i++)
        {
            byte index = indices[i];
            (byte r, byte g, byte b) = LookupPalette(palette, index);
            int offset = i * 4;
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private static void BlitIndices(
        byte[] canvas,
        int canvasWidth,
        int destX,
        int destY,
        byte[] indices,
        int srcWidth,
        int srcHeight,
        (byte R, byte G, byte B)[]? palette)
    {
        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                byte index = indices[y * srcWidth + x];
                (byte r, byte g, byte b) = LookupPalette(palette, index);
                int offset = ((destY + y) * canvasWidth + (destX + x)) * 4;
                canvas[offset] = b;
                canvas[offset + 1] = g;
                canvas[offset + 2] = r;
                canvas[offset + 3] = 255;
            }
        }
    }

    private static (byte R, byte G, byte B) LookupPalette((byte R, byte G, byte B)[]? palette, byte index)
    {
        if (palette is not null && index < palette.Length)
        {
            return palette[index];
        }

        return (0, 0, 0);
    }
}
