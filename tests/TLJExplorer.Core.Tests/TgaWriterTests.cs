using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class TgaWriterTests
{
    private const int HeaderSize = 18;
    private const int FooterSize = 26;

    private static DecodedImage MakeImage(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> pixelAt)
    {
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            (byte b, byte g, byte r, byte a) = pixelAt(x, y);
            int o = ((y * w) + x) * 4;
            pixels[o + 0] = b;
            pixels[o + 1] = g;
            pixels[o + 2] = r;
            pixels[o + 3] = a;
        }

        return new DecodedImage(w, h, pixels);
    }

    [Fact]
    public void Write_Uncompressed_HeaderDeclaresImageType2AndDimensions()
    {
        DecodedImage image = MakeImage(4, 3, (_, _) => (0, 0, 0, 255));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: false);

        byte[] bytes = stream.ToArray();
        Assert.Equal(2, bytes[2]); // ImageType = uncompressed true-color
        Assert.Equal(4, BitConverter.ToUInt16(bytes, 12)); // Width
        Assert.Equal(3, BitConverter.ToUInt16(bytes, 14)); // Height
        Assert.Equal(32, bytes[16]); // PixelDepth
        Assert.Equal(0x28, bytes[17]); // ImageDescriptor
    }

    [Fact]
    public void Write_Uncompressed_PixelDataIsRawBgraCopy()
    {
        DecodedImage image = MakeImage(2, 1, (x, _) => x == 0 ? ((byte)1, (byte)2, (byte)3, (byte)4) : ((byte)5, (byte)6, (byte)7, (byte)8));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: false);

        byte[] bytes = stream.ToArray();
        byte[] pixelSection = bytes[HeaderSize..(HeaderSize + image.Pixels.Length)];
        Assert.Equal(image.Pixels, pixelSection);
    }

    [Fact]
    public void Write_Uncompressed_TotalLengthIsHeaderPlusPixelsPlusFooter()
    {
        DecodedImage image = MakeImage(4, 4, (_, _) => (0, 0, 0, 255));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: false);

        Assert.Equal(HeaderSize + image.Pixels.Length + FooterSize, stream.Length);
    }

    [Fact]
    public void Write_AppendsTruevisionFooterSignature()
    {
        DecodedImage image = MakeImage(1, 1, (_, _) => (0, 0, 0, 255));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: false);

        byte[] bytes = stream.ToArray();
        string signature = System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 18, 18).TrimEnd('\0');
        Assert.Equal("TRUEVISION-XFILE.", signature);
    }

    [Fact]
    public void Write_Rle_HeaderDeclaresImageType10()
    {
        DecodedImage image = MakeImage(4, 4, (_, _) => (0, 0, 0, 255));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: true);

        Assert.Equal(10, stream.ToArray()[2]);
    }

    [Fact]
    public void Write_Rle_UniformRow_CompressesToRunPacket()
    {
        // A single-color 130-pixel row needs two run packets (max run length 128), each 5 bytes
        // (1 header + 4-byte pixel) -- far smaller than 130*4 raw bytes.
        DecodedImage image = MakeImage(130, 1, (_, _) => (9, 9, 9, 255));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: true);

        int pixelSectionLength = (int)stream.Length - HeaderSize - FooterSize;
        Assert.Equal(10, pixelSectionLength); // two run packets: 5 bytes each
    }

    [Fact]
    public void Write_Rle_AlternatingPixels_UsesRawPacket()
    {
        DecodedImage image = MakeImage(4, 1, (x, _) => x % 2 == 0 ? ((byte)1, (byte)1, (byte)1, (byte)255) : ((byte)2, (byte)2, (byte)2, (byte)255));
        using var stream = new MemoryStream();

        TgaWriter.Write(image, stream, useRle: true);

        int pixelSectionLength = (int)stream.Length - HeaderSize - FooterSize;
        Assert.Equal(1 + (4 * 4), pixelSectionLength); // one raw packet header + 4 literal pixels
    }

    [Fact]
    public void Write_NullImage_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => TgaWriter.Write(null!, stream));
    }

    [Fact]
    public void Write_NullStream_Throws()
    {
        DecodedImage image = MakeImage(1, 1, (_, _) => (0, 0, 0, 255));
        Assert.Throws<ArgumentNullException>(() => TgaWriter.Write(image, (Stream)null!));
    }

    [Fact]
    public void Write_ToFilePath_CreatesReadableFile()
    {
        DecodedImage image = MakeImage(2, 2, (_, _) => (1, 2, 3, 4));
        string path = Path.Combine(Path.GetTempPath(), $"tlj-tga-test-{Guid.NewGuid():N}.tga");
        try
        {
            TgaWriter.Write(image, path);

            Assert.True(File.Exists(path));
            Assert.Equal(HeaderSize + image.Pixels.Length + FooterSize, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
