namespace TLJExplorer.Core.Formats;

/// <summary>
/// Computes a pixel-by-pixel difference between two images on a shared canvas.
/// Output is a <see cref="DecodedImage"/> in BGRA32 layout where identical pixels are solid black
/// and differences glow in the color of the delta, with optional amplification gain.
/// </summary>
public static class ImageDifference
{
    /// <summary>
    /// Computes the absolute difference (|original - modded|) per pixel over a canvas of size
    /// <paramref name="canvasWidth"/> x <paramref name="canvasHeight"/>.
    /// </summary>
    /// <param name="original">The original image.</param>
    /// <param name="modded">The modded image.</param>
    /// <param name="canvasWidth">Width of the comparison canvas.</param>
    /// <param name="canvasHeight">Height of the comparison canvas.</param>
    /// <param name="gain">Multiplier applied to color deltas (clamped to at least 1).</param>
    /// <param name="differingPixels">Count of pixels with non-zero difference.</param>
    /// <returns>A new <see cref="DecodedImage"/> containing the difference bitmap.</returns>
    public static DecodedImage ComputeDifference(
        DecodedImage original,
        DecodedImage modded,
        int canvasWidth,
        int canvasHeight,
        int gain,
        out int differingPixels)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modded);
        if (canvasWidth <= 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        if (canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(canvasHeight));
        if (gain < 1) gain = 1;

        byte[] diffPixels = new byte[checked(canvasWidth * canvasHeight * 4)];
        int diffCount = 0;

        int origW = original.Width;
        int origH = original.Height;
        byte[] origPixels = original.Pixels;

        int modW = modded.Width;
        int modH = modded.Height;
        byte[] modPixels = modded.Pixels;

        bool sameDimensions = origW == canvasWidth && origH == canvasHeight &&
                             modW == canvasWidth && modH == canvasHeight;

        if (sameDimensions)
        {
            int totalPixels = canvasWidth * canvasHeight;
            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;
                byte ob = origPixels[offset];
                byte og = origPixels[offset + 1];
                byte or_ = origPixels[offset + 2];
                byte oa = origPixels[offset + 3];

                byte mb = modPixels[offset];
                byte mg = modPixels[offset + 1];
                byte mr = modPixels[offset + 2];
                byte ma = modPixels[offset + 3];

                ComputePixelDiff(ob, og, or_, oa, mb, mg, mr, ma, gain,
                    out byte outB, out byte outG, out byte outR, out bool differs);

                diffPixels[offset] = outB;
                diffPixels[offset + 1] = outG;
                diffPixels[offset + 2] = outR;
                diffPixels[offset + 3] = 255;

                if (differs)
                    diffCount++;
            }
        }
        else
        {
            for (int y = 0; y < canvasHeight; y++)
            {
                int origY = origH == canvasHeight ? y : Math.Clamp((int)((long)y * origH / canvasHeight), 0, origH - 1);
                int modY = modH == canvasHeight ? y : Math.Clamp((int)((long)y * modH / canvasHeight), 0, modH - 1);

                int dstRowOffset = y * canvasWidth * 4;
                int origRowOffset = origY * origW * 4;
                int modRowOffset = modY * modW * 4;

                for (int x = 0; x < canvasWidth; x++)
                {
                    int origX = origW == canvasWidth ? x : Math.Clamp((int)((long)x * origW / canvasWidth), 0, origW - 1);
                    int modX = modW == canvasWidth ? x : Math.Clamp((int)((long)x * modW / canvasWidth), 0, modW - 1);

                    int origOffset = origRowOffset + origX * 4;
                    int modOffset = modRowOffset + modX * 4;
                    int dstOffset = dstRowOffset + x * 4;

                    byte ob = origPixels[origOffset];
                    byte og = origPixels[origOffset + 1];
                    byte or_ = origPixels[origOffset + 2];
                    byte oa = origPixels[origOffset + 3];

                    byte mb = modPixels[modOffset];
                    byte mg = modPixels[modOffset + 1];
                    byte mr = modPixels[modOffset + 2];
                    byte ma = modPixels[modOffset + 3];

                    ComputePixelDiff(ob, og, or_, oa, mb, mg, mr, ma, gain,
                        out byte outB, out byte outG, out byte outR, out bool differs);

                    diffPixels[dstOffset] = outB;
                    diffPixels[dstOffset + 1] = outG;
                    diffPixels[dstOffset + 2] = outR;
                    diffPixels[dstOffset + 3] = 255;

                    if (differs)
                        diffCount++;
                }
            }
        }

        differingPixels = diffCount;
        return new DecodedImage(canvasWidth, canvasHeight, diffPixels);
    }

    private static void ComputePixelDiff(
        byte ob, byte og, byte or_, byte oa,
        byte mb, byte mg, byte mr, byte ma,
        int gain,
        out byte outB, out byte outG, out byte outR, out bool differs)
    {
        if (oa == 0 && ma == 0)
        {
            outB = 0;
            outG = 0;
            outR = 0;
            differs = false;
            return;
        }

        int dr, dg, db, da;
        if (oa == 255 && ma == 255)
        {
            dr = Math.Abs(or_ - mr);
            dg = Math.Abs(og - mg);
            db = Math.Abs(ob - mb);
            da = 0;
        }
        else
        {
            int pOr = (or_ * oa + 127) / 255;
            int pOg = (og * oa + 127) / 255;
            int pOb = (ob * oa + 127) / 255;

            int pMr = (mr * ma + 127) / 255;
            int pMg = (mg * ma + 127) / 255;
            int pMb = (mb * ma + 127) / 255;

            dr = Math.Abs(pOr - pMr);
            dg = Math.Abs(pOg - pMg);
            db = Math.Abs(pOb - pMb);
            da = Math.Abs(oa - ma);

            if (dr == 0 && dg == 0 && db == 0 && da > 0)
            {
                dr = da;
                dg = da;
                db = da;
            }
        }

        differs = dr > 0 || dg > 0 || db > 0 || da > 0;

        if (gain > 1)
        {
            dr = Math.Min(255, dr * gain);
            dg = Math.Min(255, dg * gain);
            db = Math.Min(255, db * gain);
        }

        outR = (byte)dr;
        outG = (byte)dg;
        outB = (byte)db;
    }
}
