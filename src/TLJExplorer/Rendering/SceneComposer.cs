using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TLJExplorer.Services;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Core.Settings;

namespace TLJExplorer.Rendering;

/// <summary>
/// Composites the layers of an <see cref="XrcSceneModel"/> scene into a base <see cref="BitmapSource"/>
/// (backdrop + static XMG/TM sprites) plus a set of animated overlays (frame-per-PNG sequences extracted
/// from the scene's Bink/Smacker layers by ffmpeg). The base is rendered once; overlays are handed off
/// to the UI to cycle on a timer.
/// </summary>
public static class SceneComposer
{
    /// <summary>Result of composing a scene: the flat base bitmap plus zero or more animated overlays.</summary>
    public sealed record Composition(
        BitmapSource Base,
        IReadOnlyList<SceneAnimatedOverlay> Overlays);

    /// <summary>
    /// Composes the scene declared in <paramref name="layers"/>. Returns null when no backdrop was found
    /// or decoded (there's no canvas to draw against). Missing/undecodable sprites are silently skipped;
    /// ffmpeg failures on animated overlays likewise skip that overlay rather than failing the whole scene.
    /// Blocking / potentially slow: ffmpeg is invoked once per animated overlay -- call from a background
    /// thread.
    /// </summary>
    public static Composition? Compose(
        IReadOnlyList<XrcSceneLayer> layers,
        FsNode sceneFolder,
        VirtualFileSystem vfs,
        AppSettings settings,
        TempFileTracker tempFiles)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(sceneFolder);
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tempFiles);

        Dictionary<string, FsNode> siblings = new(StringComparer.OrdinalIgnoreCase);
        foreach (FsNode child in sceneFolder.Children)
        {
            if ((child.NodeType & FsNodeType.File) != 0)
                siblings[child.Name] = child;
        }

        // Find and decode the first usable backdrop. It defines the canvas dimensions -- everything else
        // is drawn relative to a top-left origin at (0,0).
        DecodedImage? backdrop = null;
        int backdropIndex = -1;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Kind != XrcSceneLayerKind.Backdrop)
                continue;

            backdrop = TryDecodeStillImage(layers[i].FileName, siblings, vfs, settings);
            if (backdrop is not null)
            {
                backdropIndex = i;
                break;
            }
        }

        if (backdrop is null)
            return null;

        int width = backdrop.Width;
        int height = backdrop.Height;
        byte[] canvas = new byte[width * height * 4];

        BlitOpaque(backdrop, canvas, width, height, 0, 0);

        var overlays = new List<SceneAnimatedOverlay>();

        // Any further static images (rare) blit into the base; videos become animated overlays.
        for (int i = 0; i < layers.Count; i++)
        {
            if (i == backdropIndex)
                continue;

            XrcSceneLayer layer = layers[i];
            string ext = Path.GetExtension(layer.FileName).ToLowerInvariant();

            if (ext is ".xmg" or ".tm")
            {
                DecodedImage? still = TryDecodeStillImage(layer.FileName, siblings, vfs, settings);
                if (still is not null)
                    BlitAlpha(still, canvas, width, height, layer.X, layer.Y);
            }
            else if (ext is ".bbb" or ".sss")
            {
                SceneAnimatedOverlay? overlay = TryExtractOverlay(layer, siblings, vfs, settings, tempFiles);
                if (overlay is not null)
                    overlays.Add(overlay);
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
        bitmap.Freeze();
        return new Composition(bitmap, overlays);
    }

    /// <summary>
    /// Looks up <paramref name="fileName"/> as a sibling in the scene folder and decodes it via the
    /// same decoders <see cref="Services.ResourceLoader"/> uses. Multi-image TMs return their first entry.
    /// </summary>
    private static DecodedImage? TryDecodeStillImage(
        string fileName,
        Dictionary<string, FsNode> siblings,
        VirtualFileSystem vfs,
        AppSettings settings)
    {
        if (!siblings.TryGetValue(fileName, out FsNode? node))
            return null;

        try
        {
            using Stream stream = vfs.OpenFile(node);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();

            return ext switch
            {
                ".xmg" => XmgDecoder.Decode(stream),
                ".tm" => TmDecoder.Decode(stream, settings.ShowMipMaps) is { Count: > 0 } tm
                    ? tm[0].Image
                    : null,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a Bink/Smacker layer to a scratch temp directory of PNGs via ffmpeg, loads each frame as a
    /// frozen <see cref="BitmapSource"/>, and packages the result as a <see cref="SceneAnimatedOverlay"/>.
    /// Returns null on any failure -- missing sibling, unwrap error, ffmpeg failure, or zero-frame result.
    /// </summary>
    private static SceneAnimatedOverlay? TryExtractOverlay(
        XrcSceneLayer layer,
        Dictionary<string, FsNode> siblings,
        VirtualFileSystem vfs,
        AppSettings settings,
        TempFileTracker tempFiles)
    {
        if (!siblings.TryGetValue(layer.FileName, out FsNode? node))
            return null;

        try
        {
            // Unwrap .bbb/.sss to a raw .bik/.smk so ffmpeg can decode it.
            string ext = Path.GetExtension(layer.FileName);
            string extractedExt = ContainerUnwrap.GetExtractedExtension(ext);
            string rawPath = tempFiles.CreateTempFile(extractedExt);
            using (Stream src = vfs.OpenFile(node))
                ContainerUnwrap.ExtractToFile(src, rawPath);

            string frameDir = tempFiles.CreateTempDirectory();
            FfmpegTranscoder.FrameExtractResult result = FfmpegTranscoder.ExtractFrames(
                rawPath, settings.FfmpegPath, frameDir);

            if (!result.Success || result.FramePaths.Count == 0)
                return null;

            var frames = new List<BitmapSource>(result.FramePaths.Count);
            foreach (string path in result.FramePaths)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                frames.Add(image);
            }

            return new SceneAnimatedOverlay(layer.Name, layer.X, layer.Y, frames, result.Fps);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Blits <paramref name="src"/> onto <paramref name="dst"/> at <c>(dx,dy)</c>, ignoring alpha (opaque backdrop copy).</summary>
    private static void BlitOpaque(DecodedImage src, byte[] dst, int dstWidth, int dstHeight, int dx, int dy)
    {
        int srcStride = src.Width * 4;
        int dstStride = dstWidth * 4;

        int x0 = Math.Max(0, dx);
        int y0 = Math.Max(0, dy);
        int x1 = Math.Min(dstWidth, dx + src.Width);
        int y1 = Math.Min(dstHeight, dy + src.Height);

        for (int y = y0; y < y1; y++)
        {
            int srcRow = ((y - dy) * srcStride) + ((x0 - dx) * 4);
            int dstRow = (y * dstStride) + (x0 * 4);
            int rowBytes = (x1 - x0) * 4;
            Buffer.BlockCopy(src.Pixels, srcRow, dst, dstRow, rowBytes);
        }
    }

    /// <summary>
    /// Blits <paramref name="src"/> onto <paramref name="dst"/> at <c>(dx,dy)</c> with straight-alpha
    /// (src over dst) compositing. Pixels are BGRA8; alpha == 0 is skipped.
    /// </summary>
    private static void BlitAlpha(DecodedImage src, byte[] dst, int dstWidth, int dstHeight, int dx, int dy)
    {
        int srcStride = src.Width * 4;
        int dstStride = dstWidth * 4;

        int x0 = Math.Max(0, dx);
        int y0 = Math.Max(0, dy);
        int x1 = Math.Min(dstWidth, dx + src.Width);
        int y1 = Math.Min(dstHeight, dy + src.Height);

        byte[] sp = src.Pixels;

        for (int y = y0; y < y1; y++)
        {
            int srcRow = ((y - dy) * srcStride) + ((x0 - dx) * 4);
            int dstRow = (y * dstStride) + (x0 * 4);

            for (int x = x0; x < x1; x++, srcRow += 4, dstRow += 4)
            {
                byte sa = sp[srcRow + 3];
                if (sa == 0)
                    continue;

                if (sa == 255)
                {
                    dst[dstRow + 0] = sp[srcRow + 0];
                    dst[dstRow + 1] = sp[srcRow + 1];
                    dst[dstRow + 2] = sp[srcRow + 2];
                    dst[dstRow + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                dst[dstRow + 0] = (byte)(((sp[srcRow + 0] * sa) + (dst[dstRow + 0] * inv)) / 255);
                dst[dstRow + 1] = (byte)(((sp[srcRow + 1] * sa) + (dst[dstRow + 1] * inv)) / 255);
                dst[dstRow + 2] = (byte)(((sp[srcRow + 2] * sa) + (dst[dstRow + 2] * inv)) / 255);
                dst[dstRow + 3] = (byte)Math.Min(255, dst[dstRow + 3] + sa);
            }
        }
    }
}
