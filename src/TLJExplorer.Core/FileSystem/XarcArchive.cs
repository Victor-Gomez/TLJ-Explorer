using System.Text;

namespace TLJExplorer.Core.FileSystem;

/// <summary>
/// A single named blob stored inside a <c>.xarc</c> archive.
/// </summary>
/// <param name="Name">The entry's stored name (ASCII, as read from the archive directory).</param>
/// <param name="Size">The entry's size in bytes.</param>
/// <param name="Offset">
/// The entry's absolute byte offset within the owning <c>.xarc</c> file. This is not stored directly
/// in the archive; it is derived cumulatively from <see cref="XarcArchive"/>'s header <c>BaseOfs</c> and
/// each preceding entry's size.
/// </param>
/// <param name="IsLocalized">
/// The trailing per-entry <c>UInt32</c> is 0 for English entries and 1 for localised entries. Any
/// other value is unrecognised and left surfaced via <see cref="LocaleFlagRaw"/> unchanged.
/// </param>
/// <param name="LocaleFlagRaw">
/// The raw value of the trailing per-entry <c>UInt32</c>, retained for tooling that wants to expose
/// or filter on the flag directly.
/// </param>
public sealed record XarcEntry(string Name, int Size, long Offset, bool IsLocalized, uint LocaleFlagRaw);

/// <summary>
/// Reads the directory of a <c>.xarc</c> archive: a simple concatenated-blob container used throughout
/// the TLJ data files.
/// </summary>
/// <remarks>
/// <para>Binary layout (little-endian):</para>
/// <code>
/// Int32 Unknown
/// Int32 NumFiles
/// Int32 BaseOfs
///
/// repeat NumFiles times:
///   Name: null-terminated ASCII string
///   Int32 Size
///   UInt32 LocaleFlag    -- 0 = English, 1 = localised
/// </code>
/// <para>
/// Entry offsets are not stored in the file; they are computed cumulatively starting from
/// <c>BaseOfs</c>, with each subsequent entry immediately following the previous one's data.
/// </para>
/// </remarks>
public sealed class XarcArchive
{
    private const int MaxNameBytes = 256;

    public string ArchivePath { get; }

    public IReadOnlyList<XarcEntry> Entries { get; }

    private XarcArchive(string archivePath, IReadOnlyList<XarcEntry> entries)
    {
        ArchivePath = archivePath;
        Entries = entries;
    }

    /// <summary>
    /// Opens the <c>.xarc</c> file at <paramref name="path"/>, parses its header and entry directory,
    /// and closes the file. Entry offsets are computed cumulatively from the header's <c>BaseOfs</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// An entry name was not null-terminated within <see cref="MaxNameBytes"/> bytes (corrupt archive).
    /// </exception>
    public static XarcArchive Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var stream = File.OpenRead(path);
        return Read(stream, path);
    }

    public static XarcArchive Read(Stream stream, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        _ = reader.ReadInt32(); // Unknown
        int numFiles = reader.ReadInt32();
        int baseOfs = reader.ReadInt32();

        var entries = new List<XarcEntry>(numFiles);
        long offset = baseOfs;

        for (int i = 0; i < numFiles; i++)
        {
            string name = ReadNullTerminatedAscii(reader, path ?? "<stream>");
            int size = reader.ReadInt32();
            uint localeFlag = reader.ReadUInt32();

            entries.Add(new XarcEntry(name, size, offset, localeFlag == 1, localeFlag));
            offset += size;
        }

        return new XarcArchive(path ?? string.Empty, entries);
    }

    private static string ReadNullTerminatedAscii(BinaryReader reader, string path)
    {
        var bytes = new List<byte>(64);

        for (int i = 0; i < MaxNameBytes; i++)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                return Encoding.ASCII.GetString(bytes.ToArray());

            bytes.Add(b);
        }

        throw new InvalidDataException(
            $"'{path}' is not a valid XARC archive: an entry name was not null-terminated within {MaxNameBytes} bytes.");
    }
}
