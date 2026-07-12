namespace TLJExplorer.Core.Formats;

/// <summary>
/// An engine-agnostic decoded pixel buffer shared by all format decoders in
/// <see cref="TLJExplorer.Core.Formats"/>. Pixel data is tightly packed BGRA32
/// (4 bytes per pixel, order Blue/Green/Red/Alpha), stored top-down and
/// row-major with a stride of <c>Width * 4</c> bytes. This layout maps
/// directly onto WPF's <c>PixelFormats.Bgra32</c> and most modern imaging
/// libraries.
/// </summary>
public sealed class DecodedImage
{
    /// <summary>Width of the image, in pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the image, in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Tightly packed BGRA32 pixel data, top-down, row-major.
    /// Length is always <c>Width * Height * 4</c>.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>Optional sub-image name, if the container format provides one.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional transparent/background color defined by the source format,
    /// packed as <c>0x00BBGGRR</c> is NOT used here — for simplicity this is
    /// stored as <c>(uint)(R | (G &lt;&lt; 8) | (B &lt;&lt; 16))</c>, i.e. the low
    /// byte is Red, the next byte Green, the next byte Blue, and the top byte
    /// is unused/zero. Consumers that need a specific packed order should
    /// unpack via <c>R = value &amp; 0xFF</c>, <c>G = (value &gt;&gt; 8) &amp; 0xFF</c>,
    /// <c>B = (value &gt;&gt; 16) &amp; 0xFF</c>. <c>null</c> if the format defines no
    /// such color.
    /// </summary>
    public uint? TransparentColorBgr { get; init; }

    public DecodedImage(int width, int height, byte[] pixels)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(pixels);

        var expectedLength = checked(width * height * 4);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Pixel buffer length {pixels.Length} does not match Width*Height*4 ({expectedLength}).",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }
}
