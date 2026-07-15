using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Services;

/// <summary>
/// Writes a <see cref="DecodedImage"/> out as a PNG file. Builds an Avalonia <see cref="Bitmap"/> directly
/// over the source BGRA32 buffer (unpremultiplied alpha, matching <see cref="DecodedImage"/>'s layout) so
/// alpha survives end-to-end, then lets Avalonia's Skia-backed encoder do the PNG write.
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

        using Bitmap bitmap = ToBitmap(image);
        bitmap.Save(output);
    }

    /// <summary>Wraps <paramref name="image"/>'s pixel buffer in an Avalonia <see cref="Bitmap"/> (the
    /// constructor copies the data out immediately, so the buffer can be reused/discarded afterwards).</summary>
    internal static unsafe Bitmap ToBitmap(DecodedImage image)
    {
        int stride = image.Width * 4;
        fixed (byte* p = image.Pixels)
        {
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul,
                (nint)p,
                new PixelSize(image.Width, image.Height),
                new Vector(96, 96),
                stride);
        }
    }
}
