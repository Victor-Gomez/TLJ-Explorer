using System.Globalization;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Fallback viewer for files that don't match any known TLJ resource format:
/// reads the raw bytes and renders a classic hex/ASCII dump for inspection.
/// </summary>
public static class RawFormat
{
    private const int MaxDumpBytes = 128 * 1024; // Only the first 128 KiB are ever rendered as text.

    /// <summary>Reads <paramref name="stream"/>'s remaining bytes into memory.</summary>
    public static byte[] ReadAll(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Produces a classic hex-dump of (at most) the first 128 KiB of <paramref name="data"/>.
    /// Each line has the form:
    /// <c>XXXX:XXXX | b0 b1 b2 ... b15 | c0c1c2...c15</c>
    /// where the offset is a 32-bit value split into two 4-hex-digit groups (e.g. offset 0x00012345
    /// renders as "0001:2345"), each byte is shown as two hex digits separated by a space, and the
    /// character column shows the ASCII character for byte values 33..127 inclusive, or '.' otherwise.
    /// </summary>
    public static string ToHexDump(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        int length = Math.Min(data.Length, MaxDumpBytes);
        var sb = new StringBuilder();

        for (int offset = 0; offset < length; offset += 16)
        {
            int lineLength = Math.Min(16, length - offset);

            uint offsetValue = (uint)offset;
            sb.Append(((offsetValue >> 16) & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append((offsetValue & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture));
            sb.Append(" | ");

            for (int i = 0; i < 16; i++)
            {
                if (i < lineLength)
                {
                    sb.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("  ");
                }

                sb.Append(' ');
            }

            sb.Append("| ");

            for (int i = 0; i < lineLength; i++)
            {
                byte b = data[offset + i];
                sb.Append(b is >= 33 and <= 127 ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
