using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class XmgDecoderTests
{
    private static MemoryStream BuildHeader(uint width, uint height, byte voidR = 0, byte voidG = 255, byte voidB = 0)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        w.Write(3u); // Version
        w.Write(voidR);
        w.Write(voidG);
        w.Write(voidB);
        w.Write((byte)0); // Empty/padding
        w.Write(width);
        w.Write(height);
        w.Write(width * 3); // LineLen
        w.Write(0u); // Unknown3
        w.Write(0u); // Unknown4

        return stream;
    }

    private static DecodedImage Decode(MemoryStream stream)
    {
        stream.Position = 0;
        return XmgDecoder.Decode(stream);
    }

    [Fact]
    public void Decode_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => XmgDecoder.Decode(null!));
    }

    [Fact]
    public void Decode_WrongVersion_ThrowsFormatException()
    {
        var stream = new MemoryStream();
        new BinaryWriter(stream).Write(4u);
        stream.Position = 0;

        FormatException ex = Assert.Throws<FormatException>(() => XmgDecoder.Decode(stream));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_MismatchedLineLen_ThrowsFormatException()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(3u);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(4u); // Width
        w.Write(4u); // Height
        w.Write(99u); // LineLen -- wrong, should be Width*3=12
        w.Write(0u); w.Write(0u);
        stream.Position = 0;

        Assert.Throws<FormatException>(() => XmgDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_EmptyOpcodeStream_TerminatedByEndOfStream_ProducesAllVoidImage()
    {
        MemoryStream stream = BuildHeader(2, 2, voidR: 10, voidG: 20, voidB: 30);
        // No opcodes at all -- decoder should stop cleanly when it hits end-of-stream.

        DecodedImage image = Decode(stream);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(0x00_1E140Au, image.TransparentColorBgr); // packed as R | G<<8 | B<<16
    }

    [Fact]
    public void Decode_ExplicitEndOfStreamOpcode_StopsDecoding()
    {
        MemoryStream stream = BuildHeader(2, 2);
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        Assert.Equal(2 * 2 * 4, image.Pixels.Length);
    }

    [Fact]
    public void Decode_VoidModeBlock_FillsBlockWithVoidColorAndZeroAlpha()
    {
        MemoryStream stream = BuildHeader(2, 2, voidR: 1, voidG: 2, voidB: 3);
        stream.WriteByte(0x40 | 1); // Void mode, run length 1
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        for (int i = 0; i < 4; i++)
        {
            int o = i * 4;
            Assert.Equal(3, image.Pixels[o + 0]); // B
            Assert.Equal(2, image.Pixels[o + 1]); // G
            Assert.Equal(1, image.Pixels[o + 2]); // R
            Assert.Equal(0, image.Pixels[o + 3]); // A -- void is always transparent
        }
    }

    [Fact]
    public void Decode_RgbModeBlock_WritesFourDistinctOpaquePixels()
    {
        MemoryStream stream = BuildHeader(2, 2, voidR: 0, voidG: 0, voidB: 0);
        stream.WriteByte(0x80 | 1); // RGB mode, run length 1
        // top-left, top-right, bottom-left, bottom-right, each R,G,B.
        byte[] rgb = [10, 20, 30, /*TR*/ 40, 50, 60, /*BL*/ 70, 80, 90, /*BR*/ 100, 110, 120];
        stream.Write(rgb);
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        AssertPixel(image, 0, 0, r: 10, g: 20, b: 30, a: 255);
        AssertPixel(image, 1, 0, r: 40, g: 50, b: 60, a: 255);
        AssertPixel(image, 0, 1, r: 70, g: 80, b: 90, a: 255);
        AssertPixel(image, 1, 1, r: 100, g: 110, b: 120, a: 255);
    }

    [Fact]
    public void Decode_YuvModeBlock_NeutralChroma_ReproducesLumaAsGray()
    {
        MemoryStream stream = BuildHeader(2, 2);
        stream.WriteByte(0x00 | 1); // YUV mode, run length 1
        stream.WriteByte(128); // Y0
        stream.WriteByte(128); // Y1
        stream.WriteByte(128); // Y2
        stream.WriteByte(128); // Y3
        stream.WriteByte(128); // Cr (neutral -> signed 0)
        stream.WriteByte(128); // Cb (neutral -> signed 0)
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        AssertPixel(image, 0, 0, r: 128, g: 128, b: 128, a: 255);
    }

    [Fact]
    public void Decode_ExtendedOpcodeVoidMode_RunsAcrossMultipleBlocks()
    {
        // Width=8 (4 blocks/row), height=2 (1 block row): fill all 4 blocks with one extended
        // void-mode opcode, run length 4. Extended void: top nibble 0b1101 (0xC0|(1<<4)), low nibble
        // + second byte encode run length; use run length 4 (0x004).
        MemoryStream stream = BuildHeader(8, 2, voidR: 5, voidG: 6, voidB: 7);
        byte opcode = (byte)(0xC0 | (1 << 4) | 0x0); // mode=1 (Void), high nibble of run length = 0
        stream.WriteByte(opcode);
        stream.WriteByte(4); // low byte of run length -> total run length 4
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        for (int x = 0; x < 8; x++)
        {
            AssertPixel(image, x, 0, r: 5, g: 6, b: 7, a: 0);
            AssertPixel(image, x, 1, r: 5, g: 6, b: 7, a: 0);
        }
    }

    [Fact]
    public void Decode_OddWidthAndHeight_ClipsOverhangingBlockPixelsSilently()
    {
        MemoryStream stream = BuildHeader(1, 1, voidR: 9, voidG: 9, voidB: 9);
        stream.WriteByte(0x40 | 1); // Void mode, run length 1 -- writes a 2x2 block into a 1x1 image
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        AssertPixel(image, 0, 0, r: 9, g: 9, b: 9, a: 0);
    }

    [Fact]
    public void Decode_PixelThatHappensToMatchVoidColor_BecomesTransparentEvenFromRgbMode()
    {
        MemoryStream stream = BuildHeader(2, 2, voidR: 10, voidG: 20, voidB: 30);
        stream.WriteByte(0x80 | 1); // RGB mode
        // top-left pixel exactly matches the void color; others don't.
        byte[] rgb = [10, 20, 30, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        stream.Write(rgb);
        stream.WriteByte(0xFF);

        DecodedImage image = Decode(stream);

        AssertPixel(image, 0, 0, r: 10, g: 20, b: 30, a: 0);
        AssertPixel(image, 1, 0, r: 1, g: 1, b: 1, a: 255);
    }

    private static void AssertPixel(DecodedImage image, int x, int y, byte r, byte g, byte b, byte a)
    {
        int o = ((y * image.Width) + x) * 4;
        Assert.Equal(b, image.Pixels[o + 0]);
        Assert.Equal(g, image.Pixels[o + 1]);
        Assert.Equal(r, image.Pixels[o + 2]);
        Assert.Equal(a, image.Pixels[o + 3]);
    }
}
