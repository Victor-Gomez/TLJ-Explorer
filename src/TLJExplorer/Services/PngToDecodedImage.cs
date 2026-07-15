using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Services;

/// <summary>
/// Bridges PNG bytes into the <see cref="DecodedImage"/> shape our image consumers expect. Used to
/// support Stark's PNG-overrides-XMG mod convention (see <c>engines/stark/resources/image.cpp</c>
/// <c>loadPNGOverride</c>): a mod file at <c>&lt;archiveDir&gt;/xarc/&lt;name&gt;.png</c> substitutes for
/// the archive's <c>&lt;name&gt;.xmg</c>, and the decode path needs to switch codec based on the bytes.
/// </summary>
public static class PngToDecodedImage
{
    /// <summary>PNG file signature — 89 50 4E 47 0D 0A 1A 0A.</summary>
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns true when the first 8 bytes of <paramref name="peek"/> match the PNG signature.</summary>
    public static bool LooksLikePng(ReadOnlySpan<byte> peek)
    {
        if (peek.Length < PngMagic.Length)
            return false;
        for (int i = 0; i < PngMagic.Length; i++)
            if (peek[i] != PngMagic[i]) return false;
        return true;
    }

    /// <summary>
    /// Decodes a PNG stream into a <see cref="DecodedImage"/> (BGRA32). Passes through Avalonia's
    /// Skia-backed <see cref="Bitmap"/> decoder because our Core library deliberately doesn't ship a PNG
    /// codec, then transcodes into a Bgra32/Unpremul framebuffer regardless of the PNG's native pixel
    /// format (paletted, grayscale, RGB, ...).
    /// </summary>
    public static DecodedImage Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var bitmap = new Bitmap(stream);
        using var target = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        int stride = width * 4;
        var pixels = new byte[stride * height];

        using (var fb = target.Lock())
        {
            bitmap.CopyPixels(fb, AlphaFormat.Unpremul);
            if (fb.RowBytes == stride)
            {
                Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * stride, stride);
            }
        }

        return new DecodedImage(width, height, pixels);
    }

    /// <summary>
    /// Sniffs <paramref name="stream"/>'s first few bytes: if it's PNG, decode as PNG; otherwise call
    /// <paramref name="fallback"/> with a stream rewound to the start. Requires a seekable stream.
    /// </summary>
    public static DecodedImage DecodeXmgOrPng(Stream stream, Func<Stream, DecodedImage> fallback)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fallback);

        if (!stream.CanSeek)
            return fallback(stream);

        long origin = stream.Position;
        Span<byte> peek = stackalloc byte[8];
        int read = stream.Read(peek);
        stream.Position = origin;

        if (read == peek.Length && LooksLikePng(peek))
            return Decode(stream);

        return fallback(stream);
    }
}
