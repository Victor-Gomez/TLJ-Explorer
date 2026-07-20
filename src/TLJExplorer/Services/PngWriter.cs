using System.IO;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Copies <paramref name="image"/>'s BGRA32 (unpremultiplied) pixels into a fresh
    /// <see cref="WriteableBitmap"/>. Safe to call from a background thread; the returned bitmap owns its
    /// own buffer, so the source <see cref="DecodedImage"/> can be discarded immediately.
    /// </summary>
    /// <remarks>
    /// The previous implementation handed Avalonia a pointer to the managed pixel array pinned inside a
    /// <c>fixed</c> block, which was only valid for the duration of the <see cref="Bitmap"/> constructor
    /// call. Skia's backing doesn't always copy the data synchronously, so once the fixed block exited
    /// and (in the scene-compose path) the containing <c>byte[]</c> local went out of scope on its
    /// background thread, GC could move or collect the array while Skia still held the raw pointer --
    /// producing a black scene at render time. <see cref="WriteableBitmap.Lock"/> gives us a stable
    /// unmanaged backing buffer that we copy into row-by-row, matching what WPF's <c>BitmapSource.Create</c>
    /// did before the Avalonia migration.
    /// </remarks>
    internal static Bitmap ToBitmap(DecodedImage image)
    {
        var wb = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        int srcStride = image.Width * 4;
        using ILockedFramebuffer fb = wb.Lock();
        for (int y = 0; y < image.Height; y++)
            Marshal.Copy(image.Pixels, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);

        return wb;
    }
}
