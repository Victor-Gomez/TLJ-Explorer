using System.IO;
using Avalonia.Media.Imaging;
using TLJExplorer.Services;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Core.Settings;

namespace TLJExplorer.Rendering;

/// <summary>
/// Composites the layers of an <see cref="XrcSceneModel"/> scene into a base <see cref="Bitmap"/>
/// (backdrop + static XMG/TM sprites) plus a set of animated overlays (frame-per-PNG sequences extracted
/// from the scene's Bink/Smacker layers by ffmpeg). The base is rendered once; overlays are handed off
/// to the UI to cycle on a timer.
/// </summary>
public static class SceneComposer
{
    /// <summary>Result of composing a scene: the flat base bitmap plus zero or more animated overlays.</summary>
    public sealed record Composition(
        Bitmap Base,
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

        // Backdrop discovery: XRC coordinates and each sprite's hotspot are authored against the ORIGINAL
        // asset resolution. When Stark HD mods replace the backdrop with an upscaled PNG (e.g. 3556x2028
        // vs the shipped 1920x1095), the sprite positions no longer match the canvas dimensions. So we
        // decode the original AND preferred variants of the backdrop, use the mod PNG for the canvas, and
        // remember the scale factor to apply to sprite positions and (if needed) sprite pixels below.
        DecodedSprite? backdrop = null;         // the pixels we actually paint (mod-preferred if present)
        DecodedSprite? backdropOriginal = null; // authored dims, used to compute the scale factor
        int backdropIndex = -1;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Kind != XrcSceneLayerKind.Backdrop)
                continue;

            Log.Info($"SceneComposer trying backdrop layer[{i}]=\"{layers[i].FileName}\" sibling={(siblings.ContainsKey(layers[i].FileName) ? "yes" : "no")}");
            backdropOriginal = TryDecodeStillImage(layers[i].FileName, siblings, vfs, settings, VirtualFileSystem.OpenVariant.Original);
            backdrop = TryDecodeStillImage(layers[i].FileName, siblings, vfs, settings, VirtualFileSystem.OpenVariant.Preferred);
            Log.Info($"SceneComposer backdrop layer[{i}] original={(backdropOriginal is null ? "null" : $"{backdropOriginal.Value.Image.Width}x{backdropOriginal.Value.Image.Height}")} preferred={(backdrop is null ? "null" : $"{backdrop.Value.Image.Width}x{backdrop.Value.Image.Height} premul={backdrop.Value.IsPremultiplied}")}");
            if (backdrop is not null && backdropOriginal is not null)
            {
                backdropIndex = i;
                break;
            }
        }

        if (backdrop is null || backdropOriginal is null)
        {
            Log.Warn($"SceneComposer: no usable backdrop across {layers.Count} layer(s); folder=\"{sceneFolder.Name}\"");
            return null;
        }

        // Uniform scale factor -- Stark mods upscale isotropically, so one number suffices. If the mod
        // dims exactly match the original, this is 1.0 and every downstream scale step is a no-op.
        DecodedImage backdropPixels = backdrop.Value.Image;
        DecodedImage backdropOriginalPixels = backdropOriginal.Value.Image;
        double scale = (double)backdropPixels.Width / backdropOriginalPixels.Width;
        Log.Info($"SceneComposer scale={scale:0.###} (mod {backdropPixels.Width}x{backdropPixels.Height} / original {backdropOriginalPixels.Width}x{backdropOriginalPixels.Height})");

        int width = backdropPixels.Width;
        int height = backdropPixels.Height;
        byte[] canvas = new byte[width * height * 4];

        BlitOpaque(backdropPixels, canvas, width, height, 0, 0);

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
                DecodedSprite? authored = TryDecodeStillImage(layer.FileName, siblings, vfs, settings, VirtualFileSystem.OpenVariant.Original);
                DecodedSprite? preferred = TryDecodeStillImage(layer.FileName, siblings, vfs, settings, VirtualFileSystem.OpenVariant.Preferred);
                DecodedSprite? sprite = preferred ?? authored;
                if (sprite is null)
                    continue;

                DecodedImage still = sprite.Value.Image;
                int targetW = authored is not null ? Math.Max(1, (int)Math.Round(authored.Value.Image.Width * scale)) : still.Width;
                int targetH = authored is not null ? Math.Max(1, (int)Math.Round(authored.Value.Image.Height * scale)) : still.Height;

                // Ensure premultiplied BEFORE any resample so bilinear averaging can't drag dark garbage
                // RGB stored in low-alpha edge pixels into the sprite. PNG mod overrides come from Skia
                // already in premul (many exporters bake premul into their PNGs despite the spec), so
                // premultiplying them again would double up and produce the visible black halo. XMG/TM
                // originals are genuinely straight-alpha and need explicit premultiply.
                DecodedImage premul = sprite.Value.IsPremultiplied ? still : Premultiply(still);
                if (premul.Width != targetW || premul.Height != targetH)
                    premul = ResizeBilinearPremul(premul, targetW, targetH);

                int scaledX = (int)Math.Round(layer.X * scale);
                int scaledY = (int)Math.Round(layer.Y * scale);

                Log.Info($"SceneComposer blit layer[{i}] name=\"{layer.Name}\" file=\"{layer.FileName}\" pos=({scaledX},{scaledY}) size={premul.Width}x{premul.Height} (authored={(authored is null ? "?" : $"{authored.Value.Image.Width}x{authored.Value.Image.Height}")}, srcPremul={sprite.Value.IsPremultiplied}, scale={scale:0.##})");
                BlitAlpha(premul, canvas, width, height, scaledX, scaledY);
            }
            else if (ext is ".bbb" or ".sss")
            {
                SceneAnimatedOverlay? overlay = TryExtractOverlay(layer, siblings, vfs, settings, tempFiles, scale);
                if (overlay is not null)
                    overlays.Add(overlay);
            }
        }

        Bitmap bitmap = PngWriter.ToBitmap(new DecodedImage(width, height, canvas));
        return new Composition(bitmap, overlays);
    }

    /// <summary>
    /// Looks up <paramref name="fileName"/> as a sibling in the scene folder and decodes it via the
    /// same decoders <see cref="Services.ResourceLoader"/> uses. Multi-image TMs return their first entry.
    /// </summary>
    /// <summary>Decoded image plus a flag indicating whether its RGB has already been premultiplied by
    /// alpha. PNG mod overrides come out of Skia already in that state (many art-tool exporters bake
    /// premul into their PNGs despite the PNG spec calling for straight-alpha), while XMG/TM originals
    /// are genuinely straight-alpha. The scene compositor needs to know which so it doesn't double-premul.
    /// </summary>
    private readonly record struct DecodedSprite(DecodedImage Image, bool IsPremultiplied);

    private static DecodedSprite? TryDecodeStillImage(
        string fileName,
        Dictionary<string, FsNode> siblings,
        VirtualFileSystem vfs,
        AppSettings settings,
        VirtualFileSystem.OpenVariant variant = VirtualFileSystem.OpenVariant.Preferred)
    {
        if (!siblings.TryGetValue(fileName, out FsNode? node))
        {
            Log.Warn($"TryDecodeStillImage: \"{fileName}\" not in siblings map");
            return null;
        }

        try
        {
            using Stream stream = vfs.OpenFile(node, variant);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();

            switch (ext)
            {
                case ".xmg":
                    // Stark mods replace .xmg archive entries with PNG files (see ResourceLoader.LoadXmg),
                    // so we can't call XmgDecoder unconditionally -- the bytes may actually be PNG. Sniff
                    // the signature and dispatch to the right decoder, matching the file-viewer path.
                    bool isPng = SniffIsPng(stream);
                    DecodedImage? xmg = PngToDecodedImage.DecodeXmgOrPng(stream, XmgDecoder.Decode);
                    Log.Info($"TryDecodeStillImage: \"{fileName}\" {(isPng ? "png" : "xmg")} -> {(xmg is null ? "null" : $"{xmg.Width}x{xmg.Height}")}");
                    return xmg is null ? null : new DecodedSprite(xmg, IsPremultiplied: isPng);
                case ".tm":
                    var tm = TmDecoder.Decode(stream, settings.ShowMipMaps);
                    Log.Info($"TryDecodeStillImage: \"{fileName}\" TmDecoder -> {tm?.Count ?? 0} entries");
                    return tm is { Count: > 0 }
                        ? new DecodedSprite(tm[0].Image, IsPremultiplied: false)
                        : null;
                default:
                    Log.Warn($"TryDecodeStillImage: \"{fileName}\" unsupported extension {ext}");
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"TryDecodeStillImage \"{fileName}\"", ex);
            return null;
        }
    }

    /// <summary>Peeks the first 8 bytes to detect a PNG signature, rewinding the stream to the origin so
    /// the caller can decode as usual. Requires a seekable stream.</summary>
    private static bool SniffIsPng(Stream stream)
    {
        if (!stream.CanSeek)
            return false;
        long origin = stream.Position;
        Span<byte> peek = stackalloc byte[8];
        int read = stream.Read(peek);
        stream.Position = origin;
        return read == peek.Length && PngToDecodedImage.LooksLikePng(peek);
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
        TempFileTracker tempFiles,
        double scale)
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

            var frames = new List<Bitmap>(result.FramePaths.Count);
            foreach (string path in result.FramePaths)
                frames.Add(new Bitmap(path));

            // Animated overlays live as separate Image controls on the scene Canvas. Position AND size
            // scale with the backdrop -- Avalonia's Image stretches to its Width/Height allocation, so
            // we hand back scaled dimensions and the UI layer applies them to the Image control.
            int overlayX = (int)Math.Round(layer.X * scale);
            int overlayY = (int)Math.Round(layer.Y * scale);
            int overlayW = Math.Max(1, (int)Math.Round(frames[0].PixelSize.Width * scale));
            int overlayH = Math.Max(1, (int)Math.Round(frames[0].PixelSize.Height * scale));
            return new SceneAnimatedOverlay(layer.Name, overlayX, overlayY, overlayW, overlayH, frames, result.Fps);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bilinear rescale of a BGRA8 <see cref="DecodedImage"/> whose alpha is already premultiplied. The
    /// premultiply requirement is what keeps antialiased sprite edges clean during upscaling: with
    /// straight-alpha input, bilinear averaging drags the "garbage" RGB stored in low-alpha edge pixels
    /// (usually black, from the exporter's matte) back into the visible sprite as a grey halo. Once RGB
    /// has been folded into alpha, low-alpha samples contribute proportionally small RGB, so the
    /// filtered edges stay clean.
    /// </summary>
    private static DecodedImage ResizeBilinearPremul(DecodedImage src, int targetW, int targetH)
    {
        if (src.Width == targetW && src.Height == targetH)
            return src;

        byte[] sp = src.Pixels;
        byte[] dst = new byte[targetW * targetH * 4];
        int srcW = src.Width;
        int srcH = src.Height;
        int srcStride = srcW * 4;

        // Map destination-center-of-pixel back to source-center-of-pixel coordinates so the outermost
        // dst pixels sample at the outermost src pixels (rather than half-a-src-pixel outside the image).
        double sx = (double)srcW / targetW;
        double sy = (double)srcH / targetH;

        for (int y = 0; y < targetH; y++)
        {
            double srcYf = (y + 0.5) * sy - 0.5;
            int y0 = (int)Math.Floor(srcYf);
            int y1 = y0 + 1;
            double fy = srcYf - y0;
            if (y0 < 0) { y0 = 0; fy = 0; }
            if (y1 >= srcH) { y1 = srcH - 1; fy = 1; }
            int rowY0 = y0 * srcStride;
            int rowY1 = y1 * srcStride;
            int dstRow = y * targetW * 4;

            for (int x = 0; x < targetW; x++)
            {
                double srcXf = (x + 0.5) * sx - 0.5;
                int x0 = (int)Math.Floor(srcXf);
                int x1 = x0 + 1;
                double fx = srcXf - x0;
                if (x0 < 0) { x0 = 0; fx = 0; }
                if (x1 >= srcW) { x1 = srcW - 1; fx = 1; }

                int i00 = rowY0 + x0 * 4;
                int i10 = rowY0 + x1 * 4;
                int i01 = rowY1 + x0 * 4;
                int i11 = rowY1 + x1 * 4;

                double w00 = (1 - fx) * (1 - fy);
                double w10 = fx * (1 - fy);
                double w01 = (1 - fx) * fy;
                double w11 = fx * fy;

                int di = dstRow + x * 4;
                dst[di + 0] = (byte)Math.Clamp((int)(sp[i00 + 0] * w00 + sp[i10 + 0] * w10 + sp[i01 + 0] * w01 + sp[i11 + 0] * w11 + 0.5), 0, 255);
                dst[di + 1] = (byte)Math.Clamp((int)(sp[i00 + 1] * w00 + sp[i10 + 1] * w10 + sp[i01 + 1] * w01 + sp[i11 + 1] * w11 + 0.5), 0, 255);
                dst[di + 2] = (byte)Math.Clamp((int)(sp[i00 + 2] * w00 + sp[i10 + 2] * w10 + sp[i01 + 2] * w01 + sp[i11 + 2] * w11 + 0.5), 0, 255);
                dst[di + 3] = (byte)Math.Clamp((int)(sp[i00 + 3] * w00 + sp[i10 + 3] * w10 + sp[i01 + 3] * w01 + sp[i11 + 3] * w11 + 0.5), 0, 255);
            }
        }

        return new DecodedImage(targetW, targetH, dst);
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
    /// Returns a copy of <paramref name="src"/> with its RGB channels premultiplied by alpha. Our
    /// <see cref="DecodedImage"/> is documented as straight-alpha BGRA (see XmgDecoder, TmDecoder), and
    /// PNG mod overrides come out of Skia the same way. Straight-alpha "over" compositing gives visible
    /// dark fringes on antialiased sprite edges because PNG exporters commonly leave dark garbage RGB
    /// in transparent-adjacent pixels -- multiplying those raw dark values into the destination adds a
    /// grey halo. Premultiplying up front folds the alpha into the RGB before the blit so the
    /// contribution from low-alpha pixels is proportionally small regardless of what their RGB was.
    /// </summary>
    private static DecodedImage Premultiply(DecodedImage src)
    {
        byte[] sp = src.Pixels;
        byte[] dst = new byte[sp.Length];
        for (int i = 0; i < sp.Length; i += 4)
        {
            byte a = sp[i + 3];
            if (a == 0)
            {
                // dst is already zeroed.
                continue;
            }
            if (a == 255)
            {
                dst[i + 0] = sp[i + 0];
                dst[i + 1] = sp[i + 1];
                dst[i + 2] = sp[i + 2];
                dst[i + 3] = 255;
                continue;
            }
            dst[i + 0] = (byte)((sp[i + 0] * a) / 255);
            dst[i + 1] = (byte)((sp[i + 1] * a) / 255);
            dst[i + 2] = (byte)((sp[i + 2] * a) / 255);
            dst[i + 3] = a;
        }
        return new DecodedImage(src.Width, src.Height, dst);
    }

    /// <summary>
    /// Composites a PREMULTIPLIED-alpha <paramref name="src"/> over <paramref name="dst"/> at
    /// <c>(dx,dy)</c> using premultiplied "over": <c>result.rgb = src.rgb + dst.rgb * (1 - src.a)</c>.
    /// Callers are responsible for premultiplying via <see cref="Premultiply"/> beforehand. Pixels are
    /// BGRA8; alpha == 0 is skipped.
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

                // Premultiplied "over": src already carries rgb*a, so we add it directly and attenuate
                // the destination by (1 - a). This is the fix for the dark-fringe artefact -- the old
                // straight-alpha formula multiplied src.rgb by sa a second time, so any dark garbage
                // sitting in the source's low-alpha pixels bled into the composite as a grey halo.
                int inv = 255 - sa;
                dst[dstRow + 0] = (byte)(sp[srcRow + 0] + (dst[dstRow + 0] * inv + 127) / 255);
                dst[dstRow + 1] = (byte)(sp[srcRow + 1] + (dst[dstRow + 1] * inv + 127) / 255);
                dst[dstRow + 2] = (byte)(sp[srcRow + 2] + (dst[dstRow + 2] * inv + 127) / 255);
                // Alpha: sa + da*(1 - sa/255). Backdrop is fully opaque throughout, so this collapses
                // to 255 in practice; kept general to be safe if a future caller ever blits onto a
                // partially transparent canvas.
                dst[dstRow + 3] = (byte)(sa + (dst[dstRow + 3] * inv + 127) / 255);
            }
        }
    }
}
