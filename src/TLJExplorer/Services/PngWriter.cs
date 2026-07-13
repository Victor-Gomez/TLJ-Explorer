using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Services;

/// <summary>
/// Writes a <see cref="DecodedImage"/> out as a PNG file. Uses WPF's <see cref="PngBitmapEncoder"/>
/// so alpha in the source BGRA32 buffer is preserved end-to-end (transparent pixels stay transparent).
/// </summary>
public static class PngWriter
{
    public static void Write(DecodedImage image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(path);

        using var stream = File.Create(path);
        Write(image, stream);
    }

    public static void Write(DecodedImage image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        BitmapSource bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96, 96,
            PixelFormats.Bgra32,
            palette: null,
            image.Pixels,
            stride: image.Width * 4);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }
}
