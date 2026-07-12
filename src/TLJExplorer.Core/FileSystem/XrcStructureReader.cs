using System.Text;

namespace TLJExplorer.Core.FileSystem;

/// <summary>
/// A virtual sub-folder ("location") declared inside an <c>.xrc</c> file, used to graft synthetic
/// directory nodes into the virtual file system tree.
/// </summary>
public sealed record XrcLocation(int Id, string Name);

/// <summary>
/// A reference from an <c>.xrc</c> file to an on-disk archive entry (a sound, animation, or dialogue
/// clip), used to attach friendly names/extended info to the matching <see cref="FsNode"/> file node.
/// </summary>
public sealed record XrcFileRef(string Name, string? FriendlyName, string[]? ExtendedInfo);

/// <summary>
/// Lightweight structural reader for <c>.xrc</c> files: extracts only the "locations" and file
/// references (sounds/animations/dialogue) needed to build the virtual file system tree.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally distinct from <c>TLJExplorer.Core.Formats.XrcDisplayDump</c>, which decodes far
/// more record types for human-readable inspection; the two must not be confused or share code.
/// </para>
/// <para>Binary layout of a <c>TXRCRecord</c> (little-endian):</para>
/// <code>
/// Byte   TypeID
/// Byte   Tag1
/// UInt16 Tag2
/// UInt16 NameLen
/// char[NameLen] Name     -- ASCII, exact character count, no null terminator
/// Int32  DataSize
/// byte[DataSize] Data
/// UInt16 NumChildren
/// UInt16 Unknown3        -- expected 0; a non-zero value indicates a structural anomaly
/// </code>
/// <para>
/// If <c>NumChildren &gt; 0</c>, that many complete <c>TXRCRecord</c>s follow immediately afterwards in
/// the stream (each of which may itself have children, recursively).
/// </para>
/// </remarks>
public sealed class XrcStructure
{
    public string Name { get; init; } = "";

    public int Id { get; init; }

    public List<XrcLocation> Locations { get; } = [];

    public List<XrcFileRef> Files { get; } = [];

    private const byte TypeLocationA = 0x02;
    private const byte TypeLocationB = 0x03;
    private const byte TypeAnimation = 0x0b;
    private const byte TypeSound = 0x10;
    private const byte TypeDialogue = 0x1d;

    /// <summary>
    /// Reads the root record from <paramref name="stream"/> (whose <c>Name</c>/<c>Tag2</c> become the
    /// returned structure's <see cref="Name"/>/<see cref="Id"/>), then recursively walks the root's
    /// subtree plus any further top-level sibling records, until end of stream or until
    /// <paramref name="maxLength"/> bytes have been consumed from the starting position (whichever
    /// comes first).
    /// </summary>
    public static XrcStructure Read(Stream stream, long? maxLength = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);

        long start = stream.Position;
        RawRecord root = ReadRecord(reader);

        var structure = new XrcStructure { Name = root.Name, Id = root.Tag2 };

        foreach (RawRecord child in root.Children)
            Visit(child, structure);

        long limit = start + (maxLength ?? (stream.Length - start));

        while (stream.Position < limit && stream.Position < stream.Length)
        {
            RawRecord sibling = ReadRecord(reader);
            Visit(sibling, structure);
        }

        return structure;
    }

    /// <summary>
    /// Reads a length-prefixed string embedded in a record's <c>Data</c> buffer: a little-endian
    /// <see cref="ushort"/> character count at <paramref name="offset"/> (an offset into
    /// <paramref name="data"/>, not the file stream), followed by that many ASCII characters.
    /// </summary>
    public static string ReadDataString(byte[] data, int offset)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (offset < 0 || offset + 2 > data.Length)
            return string.Empty;

        ushort length = BitConverter.ToUInt16(data, offset);
        int available = Math.Min((int)length, Math.Max(0, data.Length - offset - 2));
        return Encoding.Latin1.GetString(data, offset + 2, available);
    }

    private static void Visit(RawRecord record, XrcStructure structure)
    {
        switch (record.TypeId)
        {
            case TypeLocationA:
            case TypeLocationB:
                structure.Locations.Add(new XrcLocation(record.Tag2, record.Name));
                break;

            case TypeAnimation:
            {
                string fileName = ReadDataString(record.Data, 8);
                string ext = Path.GetExtension(fileName);
                if (ext.Equals(".sss", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bbb", StringComparison.OrdinalIgnoreCase))
                {
                    structure.Files.Add(new XrcFileRef(fileName, record.Name, null));
                }

                break;
            }

            case TypeSound:
            {
                string fileName = ReadDataString(record.Data, 0);
                structure.Files.Add(new XrcFileRef(fileName, record.Name, null));
                break;
            }

            case TypeDialogue:
            {
                RawRecord? soundChild = record.Children.Find(c => c.TypeId == TypeSound);
                if (soundChild is not null)
                {
                    string soundFileName = ReadDataString(soundChild.Data, 0);
                    string soundName = soundChild.Name;
                    string subtitle = ReadDataString(record.Data, 0);

                    structure.Files.Add(new XrcFileRef(
                        soundFileName,
                        soundName,
                        [$"[{soundName}]", "", subtitle]));
                }

                break;
            }

            default:
                break;
        }

        foreach (RawRecord child in record.Children)
            Visit(child, structure);
    }

    private static RawRecord ReadRecord(BinaryReader reader)
    {
        byte typeId = reader.ReadByte();
        byte tag1 = reader.ReadByte();
        ushort tag2 = reader.ReadUInt16();
        ushort nameLen = reader.ReadUInt16();
        string name = nameLen > 0 ? Encoding.Latin1.GetString(reader.ReadBytes(nameLen)) : string.Empty;
        int dataSize = reader.ReadInt32();
        byte[] data = dataSize > 0 ? reader.ReadBytes(dataSize) : [];
        ushort numChildren = reader.ReadUInt16();
        // Trailing "unknown3" field expected to be 0; a non-zero value indicates a structural anomaly.
        // The structural reader has no place to surface a warning, so it's just consumed here;
        // XrcDisplayDump reports it in its human-readable dump.
        _ = reader.ReadUInt16();

        var children = new List<RawRecord>(numChildren);
        for (int i = 0; i < numChildren; i++)
            children.Add(ReadRecord(reader));

        return new RawRecord(typeId, tag1, tag2, name, data, children);
    }

    private sealed record RawRecord(byte TypeId, byte Tag1, ushort Tag2, string Name, byte[] Data, List<RawRecord> Children);
}
