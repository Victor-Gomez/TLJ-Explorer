namespace TLJExplorer.Core.Formats;

/// <summary>
/// Decoder for the XMG picture format used by TLJ for full-screen background
/// images. XMG stores pixels as a run-length-encoded stream of opcodes that
/// operate on 2x2 pixel blocks, using either a custom YUV (YCbCr-like)
/// encoding, a flat "void"/transparent fill, or raw RGB triples.
/// </summary>
/// <remarks>
/// <para>File layout (all integers little-endian):</para>
/// <code>
/// UInt32 Version        (must be 3)
/// Byte   VoidR
/// Byte   VoidG
/// Byte   VoidB
/// Byte   Empty          (padding, ignored)
/// UInt32 Width
/// UInt32 Height
/// UInt32 LineLen        (must equal Width*3)
/// UInt32 Unknown3
/// UInt32 Unknown4
/// &lt;opcode stream, see Decode for details&gt;
/// </code>
/// <para>
/// The body is a sequence of opcodes, each encoding a run of N identical-mode 2x2 blocks. Blocks are
/// placed via an explicit pixel cursor (x, y): after each block x advances by 2, and on reaching Width,
/// x resets to 0 and y advances by 2. Precomputing <c>Width/2</c> blocks-per-row does NOT work for odd
/// Width (common for small sprites) -- the cursor must be tracked explicitly.
/// </para>
/// <para>Opcode byte dispatch (top bits select the mode):</para>
/// <list type="bullet">
/// <item><description><c>0x00-0x3F</c> (top bits 00): YUV mode, run length = opcode itself (0-63).
/// Each block consumes 6 bytes: Y0,Y1,Y2,Y3 (per sub-pixel luma, top-left/top-right/bottom-left/bottom-right),
/// then Cr, Cb shared by all 4 sub-pixels.</description></item>
/// <item><description><c>0x40-0x7F</c> (top bits 01): Void mode, run length = opcode &amp; 0x3F.
/// No extra bytes; each block is filled with the header's void color, alpha=0.</description></item>
/// <item><description><c>0x80-0xBF</c> (top bits 10): RGB mode, run length = opcode &amp; 0x3F.
/// Each block consumes 12 bytes: 4 sub-pixels (top-left, top-right, bottom-left, bottom-right) x (R,G,B).</description></item>
/// <item><description><c>0xC0-0xFE</c> (top bits 11, excluding 0xFF): extended opcode. mode = (opcode &gt;&gt; 4) &amp; 0x3
/// (0=YUV, 1=Void, 2=RGB); an extra byte is read and run length =
/// <c>((opcode &amp; 0x0F) &lt;&lt; 8) + secondByte</c>. Processed the same as the corresponding base mode.</description></item>
/// <item><description><c>0xFF</c>: end of stream.</description></item>
/// </list>
/// </remarks>
public static class XmgDecoder
{
    private const int ModeYuv = 0;
    private const int ModeVoid = 1;
    private const int ModeRgb = 2;

    public static DecodedImage Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var version = reader.ReadUInt32();
        if (version != 3)
        {
            throw new FormatException($"Unsupported XMG version {version}; expected 3.");
        }

        byte voidR = reader.ReadByte();
        byte voidG = reader.ReadByte();
        byte voidB = reader.ReadByte();
        reader.ReadByte(); // Empty/padding

        uint width = reader.ReadUInt32();
        uint height = reader.ReadUInt32();
        uint lineLen = reader.ReadUInt32();
        if (lineLen != width * 3)
        {
            throw new FormatException($"XMG LineLen {lineLen} does not match Width*3 ({width * 3}).");
        }

        _ = reader.ReadUInt32(); // Unknown3
        _ = reader.ReadUInt32(); // Unknown4

        int w = checked((int)width);
        int h = checked((int)height);
        var pixels = new byte[checked(w * h * 4)];

        int px = 0;
        int py = 0;

        while (true)
        {
            int opcodeByte = stream.ReadByte();
            if (opcodeByte < 0)
            {
                break; // end of stream: some files end here without an explicit 0xFF sentinel.
            }

            int opcode = opcodeByte;
            if (opcode == 0xFF)
            {
                break;
            }

            int mode;
            long runLength;

            if (opcode < 0x40)
            {
                mode = ModeYuv;
                runLength = opcode;
            }
            else if (opcode < 0x80)
            {
                mode = ModeVoid;
                runLength = opcode & 0x3F;
            }
            else if (opcode < 0xC0)
            {
                mode = ModeRgb;
                runLength = opcode & 0x3F;
            }
            else
            {
                // 0xC0-0xFE extended opcode.
                mode = (opcode >> 4) & 0x3;
                if (mode is not (ModeYuv or ModeVoid or ModeRgb))
                {
                    throw new FormatException($"Unexpected XMG extended opcode mode {mode} (opcode 0x{opcode:X2}).");
                }
                int secondByte = reader.ReadByte();
                runLength = ((opcode & 0x0F) << 8) + secondByte;
            }

            for (long i = 0; i < runLength; i++)
            {
                switch (mode)
                {
                    case ModeYuv:
                    {
                        byte y0 = reader.ReadByte();
                        byte y1 = reader.ReadByte();
                        byte y2 = reader.ReadByte();
                        byte y3 = reader.ReadByte();
                        sbyte cr = unchecked((sbyte)(reader.ReadByte() - 128));
                        sbyte cb = unchecked((sbyte)(reader.ReadByte() - 128));

                        WriteYuvPixel(pixels, w, h, px, py, y0, cr, cb);
                        WriteYuvPixel(pixels, w, h, px + 1, py, y1, cr, cb);
                        WriteYuvPixel(pixels, w, h, px, py + 1, y2, cr, cb);
                        WriteYuvPixel(pixels, w, h, px + 1, py + 1, y3, cr, cb);
                        break;
                    }
                    case ModeVoid:
                    {
                        WritePixel(pixels, w, h, px, py, voidR, voidG, voidB, 0);
                        WritePixel(pixels, w, h, px + 1, py, voidR, voidG, voidB, 0);
                        WritePixel(pixels, w, h, px, py + 1, voidR, voidG, voidB, 0);
                        WritePixel(pixels, w, h, px + 1, py + 1, voidR, voidG, voidB, 0);
                        break;
                    }
                    case ModeRgb:
                    {
                        ReadAndWriteRgbPixel(reader, pixels, w, h, px, py);
                        ReadAndWriteRgbPixel(reader, pixels, w, h, px + 1, py);
                        ReadAndWriteRgbPixel(reader, pixels, w, h, px, py + 1);
                        ReadAndWriteRgbPixel(reader, pixels, w, h, px + 1, py + 1);
                        break;
                    }
                }

                // Advance the block cursor exactly like the original: +2 columns, wrapping to the
                // next block row once we've reached (or, for odd Width, passed) the right edge.
                px += 2;
                if (px >= w)
                {
                    px = 0;
                    py += 2;
                }
            }
        }

        // Whole-image color-key pass: any pixel that decoded to the void color -- not just pixels
        // written by explicit Void-mode blocks -- is made transparent. Without this pass, YUV/RGB-mode
        // pixels that happen to decode to (near-)exactly the void color (common at cutout-sprite
        // edges) stay opaque and show up as a visible fringe of the background color.
        ApplyColorKeyTransparency(pixels, voidR, voidG, voidB);

        uint transparentColor = (uint)(voidR | (voidG << 8) | (voidB << 16));

        return new DecodedImage(w, h, pixels)
        {
            TransparentColorBgr = transparentColor,
        };
    }

    private static void ApplyColorKeyTransparency(byte[] pixels, byte voidR, byte voidG, byte voidB)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] == voidB && pixels[offset + 1] == voidG && pixels[offset + 2] == voidR)
            {
                pixels[offset + 3] = 0;
            }
        }
    }

    private static void ReadAndWriteRgbPixel(BinaryReader reader, byte[] pixels, int width, int height, int x, int y)
    {
        byte r = reader.ReadByte();
        byte g = reader.ReadByte();
        byte b = reader.ReadByte();
        WritePixel(pixels, width, height, x, y, r, g, b, 255);
    }

    // 16.16 fixed-point Cr/Cb -> RGB coefficients. Multiplying these into the signed cr/cb (range -128..127)
    // stays well inside Int32, and one arithmetic right shift by 16 recovers the scaled contribution. Adding
    // 32768 before the shift rounds to nearest instead of truncating toward zero, matching the prior
    // Math.Round path to within the LSB. XmgDecoder invokes this once per sub-pixel; on a full-screen
    // background that's millions of calls, so avoiding the double-precision path is a measurable win.
    private const int CrToR = 91881;    // 1.402  * 65536
    private const int CbToG = 22553;    // 0.344136 * 65536
    private const int CrToG = 46801;    // 0.714136 * 65536
    private const int CbToB = 116129;   // 1.772  * 65536
    private const int RoundBias = 32768;

    private static void WriteYuvPixel(byte[] pixels, int width, int height, int x, int y, byte lumaY, sbyte cr, sbyte cb)
    {
        int r = ClampToByte(lumaY + ((CrToR * cr + RoundBias) >> 16));
        int g = ClampToByte(lumaY - ((CbToG * cb + CrToG * cr + RoundBias) >> 16));
        int b = ClampToByte(lumaY + ((CbToB * cb + RoundBias) >> 16));
        WritePixel(pixels, width, height, x, y, (byte)r, (byte)g, (byte)b, 255);
    }

    private static int ClampToByte(int value)
    {
        if (value <= 0) return 0;
        if (value >= 255) return 255;
        return value;
    }

    private static void WritePixel(byte[] pixels, int width, int height, int x, int y, byte r, byte g, byte b, byte a)
    {
        // For odd Width/Height, the second column/row of the last block in a row/column falls one
        // pixel past the image edge -- the original just clips this silently, so we do too.
        if (x >= width || y >= height)
        {
            return;
        }

        int offset = (y * width + x) * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = a;
    }
}
