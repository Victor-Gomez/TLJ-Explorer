using System.Globalization;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Detailed text dumper for XRC "display" records.
/// </summary>
/// <remarks>
/// <para>
/// XRC data is a recursive tree of <c>TXRCRecord</c> nodes:
/// </para>
/// <code>
/// Byte   TypeId
/// Byte   Tag1
/// UInt16 Tag2
/// UInt16 NameLen
/// char[NameLen] Name     -- ASCII, exact character count, no null terminator
/// Int32  DataSize
/// byte[DataSize] Data    -- this record's own payload, interpreted per TypeId
/// UInt16 NumChildren
/// UInt16 Unknown3        -- expected 0; a non-zero value indicates a structural anomaly
/// </code>
/// <para>
/// If <c>NumChildren &gt; 0</c>, that many complete <c>TXRCRecord</c>s follow immediately afterwards in
/// the stream (each of which may itself have children, recursively).
/// </para>
/// <para>
/// This dumper is independent from (and decodes far more record types/fields than) the lighter
/// structural XRC reader used elsewhere to build the virtual file tree; the two must not be confused.
/// </para>
/// </remarks>
public static class XrcDisplayDump
{
    private const byte TypeObject = 0x08;
    private const byte TypeAnimation = 0x0b;
    private const byte TypeImage = 0x0d;
    private const byte TypeAnimScriptLine = 0x0f;
    private const byte TypeSound = 0x10;
    private const byte TypeScriptVariable = 0x15;
    private const byte TypeSubtitle = 0x1d;
    private const byte TypeModel = 0x20;
    private const byte TypeLipsync = 0x23;
    private const byte TypeStockSound = 0x24;
    private const byte TypeTexture = 0x26;

    /// <summary>
    /// Reads the full record tree from <paramref name="stream"/> (from the current position to EOF, one
    /// or more sibling root records) and renders it as an indented, brace-delimited text tree.
    /// </summary>
    public static string DumpAsText(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var sb = new StringBuilder();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            DumpRecord(reader, sb, indent: 0);
        }

        return sb.ToString();
    }

    private static void DumpRecord(BinaryReader reader, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);

        byte typeId = reader.ReadByte();
        byte tag1 = reader.ReadByte();
        _ = reader.ReadUInt16(); // Tag2
        ushort nameLen = reader.ReadUInt16();
        string name = nameLen > 0 ? Encoding.Latin1.GetString(reader.ReadBytes(nameLen)) : string.Empty;
        int dataSize = reader.ReadInt32();
        byte[] data = dataSize > 0 ? reader.ReadBytes(dataSize) : Array.Empty<byte>();
        ushort numChildren = reader.ReadUInt16();
        // Trailing "unknown3" field expected to be 0; a non-zero value indicates a structural anomaly.
        ushort unknown3 = reader.ReadUInt16();

        sb.Append(pad).Append(name).Append(" {\n");

        if (unknown3 != 0)
        {
            sb.Append(pad).Append("  [warning: unknown3 = 0x")
                .Append(unknown3.ToString("X4", CultureInfo.InvariantCulture))
                .Append(" (expected 0)]\n");
        }

        int consumed = DecodePayload(typeId, tag1, data, sb, indent + 1);
        int remaining = data.Length - consumed;
        if (remaining > 0)
        {
            sb.Append(pad).Append("  [").Append(remaining.ToString(CultureInfo.InvariantCulture))
                .Append(" bytes of unknown data]\n");
        }

        for (int i = 0; i < numChildren; i++)
        {
            DumpRecord(reader, sb, indent + 1);
        }

        sb.Append(pad).Append("}\n");
    }

    /// <summary>
    /// Decodes a record's <c>Data</c> payload according to its <c>TypeId</c>, appending human-readable
    /// lines to <paramref name="sb"/>, and returns the number of bytes consumed from the start of
    /// <paramref name="data"/> so the caller can report any undecoded trailing bytes.
    /// </summary>
    private static int DecodePayload(byte typeId, byte tag1, byte[] data, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);

        switch (typeId)
        {
            case TypeObject:
            {
                // TypeObject (0x08) is the Item resource; XrcSceneModel.ItemPositionOffset knows which
                // subtypes carry an (x, y) and at what byte offset within the payload.
                int positionOffset = XrcSceneModel.ItemPositionOffset(tag1);
                if (positionOffset < 0 || data.Length < positionOffset + 8)
                    return 0;

                int xPos = BitConverter.ToInt32(data, positionOffset);
                int yPos = BitConverter.ToInt32(data, positionOffset + 4);
                sb.Append(pad).Append("Position: (").Append(xPos.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(yPos.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                return positionOffset + 8;
            }

            case TypeAnimation:
            {
                int offset = 0;
                int category = ReadInt32(data, ref offset);
                int numFrames = ReadInt32(data, ref offset);
                var (fileName, consumed) = ReadDataString(data, offset);
                offset += consumed;

                sb.Append(pad).Append("Category: ").Append(CategoryName(category)).Append('\n');
                sb.Append(pad).Append("NumFrames: ").Append(numFrames.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(pad).Append("Animation file: \"").Append(fileName).Append("\"\n");

                if (tag1 == 3)
                {
                    int width = ReadInt32(data, ref offset);
                    int height = ReadInt32(data, ref offset);
                    int numFrames2 = ReadInt32(data, ref offset);

                    sb.Append(pad).Append("Sprite size: ").Append(width.ToString(CultureInfo.InvariantCulture))
                        .Append('x').Append(height.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(pad).Append("NumFrames2: ").Append(numFrames2.ToString(CultureInfo.InvariantCulture)).Append('\n');

                    for (int i = 0; i < numFrames2; i++)
                    {
                        int fx = ReadInt32(data, ref offset);
                        int fy = ReadInt32(data, ref offset);
                        int funk1 = ReadInt32(data, ref offset);
                        int funk2 = ReadInt32(data, ref offset);
                        int fw = ReadInt32(data, ref offset);
                        int fh = ReadInt32(data, ref offset);

                        sb.Append(pad).Append("Frame ").Append(i.ToString(CultureInfo.InvariantCulture))
                            .Append(": pos=(").Append(fx.ToString(CultureInfo.InvariantCulture)).Append(", ")
                            .Append(fy.ToString(CultureInfo.InvariantCulture)).Append("), unknown=(")
                            .Append(funk1.ToString(CultureInfo.InvariantCulture)).Append(", ")
                            .Append(funk2.ToString(CultureInfo.InvariantCulture)).Append("), size=")
                            .Append(fw.ToString(CultureInfo.InvariantCulture)).Append('x')
                            .Append(fh.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                }

                return offset;
            }

            case TypeImage:
            {
                var (fileName, consumed) = ReadDataString(data, 0);
                sb.Append(pad).Append("Image file: \"").Append(fileName).Append("\"\n");
                return consumed;
            }

            case TypeAnimScriptLine:
            {
                int offset = 0;
                int code = ReadInt32(data, ref offset);
                switch (code)
                {
                    case 0:
                    {
                        int pause = ReadInt32(data, ref offset);
                        int frame = ReadInt32(data, ref offset);
                        sb.Append(pad).Append("SHOW(pause=").Append(pause.ToString(CultureInfo.InvariantCulture))
                            .Append(", frame=").Append(frame.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                        break;
                    }
                    case 2:
                    {
                        int pause = ReadInt32(data, ref offset);
                        int line = ReadInt32(data, ref offset);
                        sb.Append(pad).Append("GOTO(pause=").Append(pause.ToString(CultureInfo.InvariantCulture))
                            .Append(", line=").Append(line.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                        break;
                    }
                    case 3:
                    {
                        int pause = ReadInt32(data, ref offset);
                        short maxFrame = ReadInt16(data, ref offset);
                        short minFrame = ReadInt16(data, ref offset);
                        sb.Append(pad).Append("SHOW_RANDOM_FRAME(pause=").Append(pause.ToString(CultureInfo.InvariantCulture))
                            .Append(", min=").Append(minFrame.ToString(CultureInfo.InvariantCulture))
                            .Append(", max=").Append(maxFrame.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                        break;
                    }
                    case 4:
                    {
                        int pause = ReadInt32(data, ref offset);
                        int maxWait = ReadInt32(data, ref offset);
                        sb.Append(pad).Append("WAIT_RANDOM_TIME(pause=").Append(pause.ToString(CultureInfo.InvariantCulture))
                            .Append(", maxWait=").Append(maxWait.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                        break;
                    }
                    default:
                        sb.Append(pad).Append("opcode ").Append(code.ToString(CultureInfo.InvariantCulture)).Append(": unknown\n");
                        break;
                }

                return offset;
            }

            case TypeSound:
            {
                var (fileName, consumed) = ReadDataString(data, 0);
                sb.Append(pad).Append("Sound file: \"").Append(fileName).Append("\"\n");
                return consumed;
            }

            case TypeScriptVariable:
            {
                if (data.Length >= 4)
                {
                    int value = BitConverter.ToInt32(data, 0);
                    sb.Append(pad).Append("Value: ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    return 4;
                }

                return 0;
            }

            case TypeSubtitle:
            {
                var (text, consumed) = ReadDataString(data, 0);
                sb.Append(pad).Append("Subtitle: \"").Append(text).Append("\"\n");
                return consumed;
            }

            case TypeModel:
            {
                var (fileName, consumed) = ReadDataString(data, 0);
                sb.Append(pad).Append("Model file: \"").Append(fileName).Append("\"\n");
                return consumed;
            }

            case TypeLipsync:
            {
                // NOTE: this stride-8 lipsync encoding is reverse-engineered from observed data and is
                // best-effort / possibly under-verified. Each sync-string byte lives 8 bytes apart,
                // offset by 4 within that stride, starting right after the leading SyncSize field.
                int offset = 0;
                int syncSize = ReadInt32(data, ref offset);

                var syncChars = new char[Math.Max(0, syncSize)];
                for (int i = 0; i < syncSize; i++)
                {
                    int byteOffset = 4 + i * 8 + 4;
                    syncChars[i] = byteOffset < data.Length ? (char)data[byteOffset] : '\0';
                }

                string sync = new(syncChars);
                sb.Append(pad).Append("Lipsync data: \"").Append(sync).Append("\"\n");

                int sequOffset = 4 + syncSize * 8;
                int seekOffset = sequOffset;
                int sequSize = ReadInt32(data, ref seekOffset);
                string sequ = ReadAsciiChars(data, seekOffset, sequSize);
                seekOffset += sequSize;

                sb.Append(pad).Append("Load sequence: \"").Append(sequ).Append("\"\n");

                return seekOffset;
            }

            case TypeStockSound:
            {
                int offset = 0;
                int duration = ReadInt32(data, ref offset);
                int localId = ReadInt32(data, ref offset);
                sb.Append(pad).Append("Duration: ").Append(duration.ToString(CultureInfo.InvariantCulture))
                    .Append(", LocalId: ").Append(localId.ToString(CultureInfo.InvariantCulture)).Append('\n');
                return offset;
            }

            case TypeTexture:
            {
                var (fileName, consumed) = ReadDataString(data, 0);
                sb.Append(pad).Append("Texture file: \"").Append(fileName).Append("\"\n");
                return consumed;
            }

            default:
                return 0;
        }
    }

    private static string CategoryName(int category) => category switch
    {
        0 => "unspecified",
        1 => "idle",
        2 => "walk",
        3 => "talk",
        _ => $"unknown category {category.ToString(CultureInfo.InvariantCulture)}",
    };

    private static int ReadInt32(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            offset = data.Length;
            return 0;
        }

        int value = BitConverter.ToInt32(data, offset);
        offset += 4;
        return value;
    }

    private static short ReadInt16(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length)
        {
            offset = data.Length;
            return 0;
        }

        short value = BitConverter.ToInt16(data, offset);
        offset += 2;
        return value;
    }

    private static string ReadAsciiChars(byte[] data, int offset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= data.Length)
            return string.Empty;

        int available = Math.Min(count, data.Length - offset);
        return Encoding.Latin1.GetString(data, offset, available);
    }

    /// <summary>
    /// Reads a length-prefixed string embedded in a record's <c>Data</c> buffer: a little-endian
    /// <see cref="ushort"/> character count at <paramref name="offset"/>, followed by that many ASCII
    /// characters. Returns the string and the number of bytes consumed (2 + length).
    /// </summary>
    public static (string Value, int BytesConsumed) ReadDataString(byte[] data, int offset)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (offset < 0 || offset + 2 > data.Length)
            return (string.Empty, 0);

        ushort length = BitConverter.ToUInt16(data, offset);
        string value = ReadAsciiChars(data, offset + 2, length);
        int consumed = 2 + Math.Min((int)length, Math.Max(0, data.Length - offset - 2));
        return (value, consumed);
    }
}
