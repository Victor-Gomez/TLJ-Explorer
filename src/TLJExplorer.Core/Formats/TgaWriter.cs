using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Writes a <see cref="DecodedImage"/> out as a 32-bit true-color Truevision
/// TGA (v2) file, BGRA order, either uncompressed or RLE-compressed.
/// </summary>
/// <remarks>
/// <para>TGA header (18 bytes, little-endian):</para>
/// <code>
/// Byte   IdLength = 0
/// Byte   ColorMapType = 0
/// Byte   ImageType = 2 (uncompressed true-color) or 10 (RLE true-color)
/// UInt16 ColorMapFirstEntryIndex = 0
/// UInt16 ColorMapLength = 0
/// Byte   ColorMapEntrySize = 0
/// UInt16 XOrigin = 0
/// UInt16 YOrigin = 0
/// UInt16 Width
/// UInt16 Height
/// Byte   PixelDepth = 32
/// Byte   ImageDescriptor = 0x28   (alpha depth 8 | top-left origin)
/// </code>
/// <para>
/// Pixel data is written top-to-bottom (matching the top-left origin flag),
/// each pixel stored as 4 bytes B,G,R,A -- which is exactly
/// <see cref="DecodedImage.Pixels"/>'s layout, so uncompressed rows are
/// written as-is.
/// </para>
/// <para>
/// For RLE (ImageType=10), each scanline is encoded independently (runs
/// never cross a scanline boundary). A run packet header byte is
/// <c>0x80 | (count-1)</c> followed by one 4-byte pixel (a repeat of
/// <c>count</c>, up to 128, identical pixels); a raw packet header byte is
/// <c>(count-1)</c> (top bit clear) followed by <c>count</c> (up to 128)
/// literal 4-byte pixels.
/// </para>
/// <para>
/// A TGA 2.0 footer (26 bytes) is always appended:
/// </para>
/// <code>
/// UInt32 ExtensionAreaOffset = 0
/// UInt32 DeveloperDirectoryOffset = 0
/// char[18] Signature = "TRUEVISION-XFILE."  (18 bytes, ASCII, includes trailing NUL)
/// </code>
/// </remarks>
public static class TgaWriter
{
    private const byte ImageTypeUncompressed = 2;
    private const byte ImageTypeRle = 10;
    private const byte ImageDescriptorTopLeft32Bit = 0x28;

    public static void Write(DecodedImage image, string path, bool useRle = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(path);

        using var stream = File.Create(path);
        Write(image, stream, useRle);
    }

    public static void Write(DecodedImage image, Stream output, bool useRle = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        WriteHeader(writer, image, useRle);

        if (useRle)
        {
            WriteRlePixelData(writer, image);
        }
        else
        {
            writer.Write(image.Pixels);
        }

        WriteFooter(writer);
    }

    private static void WriteHeader(BinaryWriter writer, DecodedImage image, bool useRle)
    {
        writer.Write((byte)0); // IdLength
        writer.Write((byte)0); // ColorMapType
        writer.Write(useRle ? ImageTypeRle : ImageTypeUncompressed);
        writer.Write((ushort)0); // ColorMapFirstEntryIndex
        writer.Write((ushort)0); // ColorMapLength
        writer.Write((byte)0);   // ColorMapEntrySize
        writer.Write((ushort)0); // XOrigin
        writer.Write((ushort)0); // YOrigin
        writer.Write(checked((ushort)image.Width));
        writer.Write(checked((ushort)image.Height));
        writer.Write((byte)32); // PixelDepth
        writer.Write(ImageDescriptorTopLeft32Bit);
    }

    private static void WriteRlePixelData(BinaryWriter writer, DecodedImage image)
    {
        int width = image.Width;
        int height = image.Height;
        var pixels = image.Pixels;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width * 4;
            int x = 0;

            while (x < width)
            {
                int runLength = 1;
                while (x + runLength < width
                       && runLength < 128
                       && PixelEquals(pixels, rowStart, x, x + runLength))
                {
                    runLength++;
                }

                if (runLength >= 2)
                {
                    writer.Write((byte)(0x80 | (runLength - 1)));
                    WritePixel(writer, pixels, rowStart, x);
                    x += runLength;
                }
                else
                {
                    int rawStart = x;
                    int rawCount = 1;
                    x++;

                    while (x < width && rawCount < 128)
                    {
                        // Stop the raw packet if the next pixel starts a run of 2+.
                        bool nextStartsRun = x + 1 < width && PixelEquals(pixels, rowStart, x, x + 1);
                        if (nextStartsRun)
                        {
                            break;
                        }

                        rawCount++;
                        x++;
                    }

                    writer.Write((byte)(rawCount - 1));
                    for (int i = 0; i < rawCount; i++)
                    {
                        WritePixel(writer, pixels, rowStart, rawStart + i);
                    }
                }
            }
        }
    }

    private static bool PixelEquals(byte[] pixels, int rowStart, int xa, int xb)
    {
        int offsetA = rowStart + xa * 4;
        int offsetB = rowStart + xb * 4;
        return pixels[offsetA] == pixels[offsetB]
               && pixels[offsetA + 1] == pixels[offsetB + 1]
               && pixels[offsetA + 2] == pixels[offsetB + 2]
               && pixels[offsetA + 3] == pixels[offsetB + 3];
    }

    private static void WritePixel(BinaryWriter writer, byte[] pixels, int rowStart, int x)
    {
        int offset = rowStart + x * 4;
        writer.Write(pixels, offset, 4);
    }

    private static void WriteFooter(BinaryWriter writer)
    {
        writer.Write((uint)0); // ExtensionAreaOffset
        writer.Write((uint)0); // DeveloperDirectoryOffset

        const string signature = "TRUEVISION-XFILE.";
        var signatureBytes = new byte[18];
        var encoded = Encoding.ASCII.GetBytes(signature);
        Array.Copy(encoded, signatureBytes, Math.Min(encoded.Length, 18));
        writer.Write(signatureBytes);
    }
}
