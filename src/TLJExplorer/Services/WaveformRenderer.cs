using System.IO;
using Avalonia;
using Avalonia.Media;

namespace TLJExplorer.Services;

/// <summary>
/// Builds a minimalist "equalizer-bar" waveform preview from a decoded 16-bit PCM WAV file — evenly
/// spaced rounded-cap vertical bars, each sized to the peak amplitude of its column, centered on the
/// horizontal midline.
/// </summary>
/// <remarks>
/// Two-phase design so an expensive audio decode doesn't run on the UI thread:
/// <list type="number">
///   <item><description><see cref="SamplePeaks"/> reads the WAV, downsamples to a fixed peak count.
///     Pure I/O + arithmetic, safe from a <c>Task.Run</c> worker.</description></item>
///   <item><description><see cref="Render"/> materialises those peaks into an
///     <see cref="IImage"/>. Must run on the UI thread.</description></item>
/// </list>
/// The whole strip is emitted as a vector <see cref="DrawingImage"/> rather than a raster bitmap, so it
/// stays crisp when the sound panel is resized and doesn't need a fixed pixel size at render time.
/// </remarks>
public static class WaveformRenderer
{
    /// <summary>
    /// Peak-sampling resolution — deliberately high so we can downsample from this fixed-size buffer to
    /// whatever bar count the current panel width calls for without touching the WAV again on resize.
    /// </summary>
    public const int MaxPeakCount = 1024;

    /// <summary>Bar stroke thickness in the vector geometry (translated to px at render time).</summary>
    private const double BarThickness = 2.0;

    /// <summary>Minimum bar half-length as a fraction of the strip's half-height, so silence still reads as a tiny pill instead of an invisible speck.</summary>
    private const double MinBarFraction = 0.05;

    /// <summary>Peak-based downsample of the file. Each returned value is the max absolute sample magnitude
    /// within its slice, normalised to <c>[0, 1]</c>. Empty array when the file isn't a canonical PCM WAV.</summary>
    public static float[] SamplePeaks(string wavPath, int barCount = MaxPeakCount)
    {
        if (!File.Exists(wavPath) || barCount <= 0)
            return [];

        (short[] samples, int channels) = TryReadPcmSamples(wavPath);
        if (samples.Length == 0)
            return [];

        int frameCount = samples.Length / channels;
        int framesPerBar = Math.Max(1, frameCount / barCount);
        int effectiveBars = Math.Min(barCount, Math.Max(1, frameCount / framesPerBar));

        var peaks = new float[effectiveBars];
        for (int i = 0; i < effectiveBars; i++)
        {
            int startFrame = i * framesPerBar;
            int endFrame = Math.Min(frameCount, startFrame + framesPerBar);
            short maxAbs = 0;
            for (int f = startFrame; f < endFrame; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    short v = samples[(f * channels) + c];
                    short mag = v == short.MinValue ? short.MaxValue : Math.Abs(v);
                    if (mag > maxAbs) maxAbs = mag;
                }
            }
            peaks[i] = maxAbs / 32767f;
        }

        return peaks;
    }

    /// <summary>
    /// Builds a vector waveform drawing from pre-sampled peaks. Call on the UI thread.
    /// </summary>
    /// <param name="peaks">Per-bar amplitudes in <c>[0, 1]</c>.</param>
    /// <param name="foreground">Bar colour. Alpha respected.</param>
    /// <param name="canvasHeight">Height of the drawing's viewbox; determines bar heights.</param>
    /// <param name="canvasWidth">Width of the drawing's viewbox; determines bar spacing.</param>
    public static IImage? Render(float[] peaks, Color foreground, double canvasWidth = 800, double canvasHeight = 48, int? barCount = null)
    {
        if (peaks is null || peaks.Length == 0 || canvasWidth <= 0 || canvasHeight <= 0)
            return null;

        // Downsample the high-resolution peak array to the target bar count. Each output bar is the max
        // over its slice of the source peaks — that keeps quiet-but-spikey signals from vanishing when
        // we display them at low density.
        int target = barCount ?? peaks.Length;
        if (target < 1) target = 1;
        if (target > peaks.Length) target = peaks.Length;
        float[] displayPeaks = target == peaks.Length ? peaks : Downsample(peaks, target);

        double centerY = canvasHeight / 2.0;
        // Reserve half-a-thickness of headroom top and bottom so the round caps of full-amplitude bars
        // don't clip against the canvas edge.
        double maxHalfLen = Math.Max(0, (canvasHeight - BarThickness) / 2.0);
        double minHalfLen = maxHalfLen * MinBarFraction;

        double step = canvasWidth / displayPeaks.Length;

        var brush = new SolidColorBrush(foreground);

        var pen = new Pen(brush, BarThickness)
        {
            LineCap = PenLineCap.Round,
        };

        var geometry = new GeometryGroup { FillRule = FillRule.NonZero };
        for (int i = 0; i < displayPeaks.Length; i++)
        {
            double x = (i + 0.5) * step;
            double halfLen = Math.Max(minHalfLen, displayPeaks[i] * maxHalfLen);
            geometry.Children.Add(new LineGeometry(
                new Point(x, centerY - halfLen),
                new Point(x, centerY + halfLen)));
        }

        var drawing = new GeometryDrawing { Geometry = geometry, Pen = pen };

        return new DrawingImage(drawing);
    }

    /// <summary>
    /// Peak-preserving downsample: each of <paramref name="target"/> output bins takes the maximum value
    /// across its slice of <paramref name="source"/>. Slice boundaries are computed in floating point so
    /// non-integer ratios don't clump aliasing on one side.
    /// </summary>
    private static float[] Downsample(float[] source, int target)
    {
        var result = new float[target];
        double scale = (double)source.Length / target;
        for (int i = 0; i < target; i++)
        {
            int start = (int)Math.Floor(i * scale);
            int end = (int)Math.Ceiling((i + 1) * scale);
            if (end <= start) end = start + 1;
            if (end > source.Length) end = source.Length;

            float m = 0f;
            for (int j = start; j < end; j++)
                if (source[j] > m) m = source[j];
            result[i] = m;
        }
        return result;
    }

    /// <summary>
    /// Convenience overload: samples <paramref name="wavPath"/> off-thread, then constructs the
    /// <see cref="ImageSource"/> on the current UI thread. Prefer the split
    /// <see cref="SamplePeaks"/> + <see cref="Render"/> pair for busy contexts.
    /// </summary>
    public static IImage? RenderFromFile(string wavPath, Color foreground, double canvasWidth = 800, double canvasHeight = 48)
    {
        float[] peaks = SamplePeaks(wavPath);
        return Render(peaks, foreground, canvasWidth, canvasHeight);
    }

    // -----------------------------------------------------------------------------------------------
    // Canonical PCM WAV reader (16-bit mono/stereo). Anything else — 24-bit, float, extensible — is
    // rejected; both IsnDecoder and OggDecoder produce the shape we accept, which is all we need.
    // -----------------------------------------------------------------------------------------------

    private static (short[] Samples, int Channels) TryReadPcmSamples(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (new string(reader.ReadChars(4)) != "RIFF") return ([], 0);
            _ = reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") return ([], 0);

            int channels = 0, bitsPerSample = 0;
            byte[]? sampleBytes = null;

            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8) break;
                string chunkId = new(reader.ReadChars(4));
                uint chunkSize = reader.ReadUInt32();
                long chunkStart = stream.Position;

                if (chunkId == "fmt ")
                {
                    ushort format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                    if (format != 1 || bitsPerSample != 16 || channels is < 1 or > 2)
                        return ([], 0);
                }
                else if (chunkId == "data")
                {
                    sampleBytes = reader.ReadBytes((int)Math.Min(chunkSize, int.MaxValue));
                    break;
                }

                stream.Position = chunkStart + chunkSize;
            }

            if (sampleBytes is null || channels == 0)
                return ([], 0);

            int sampleCount = sampleBytes.Length / 2;
            var samples = new short[sampleCount];
            Buffer.BlockCopy(sampleBytes, 0, samples, 0, sampleCount * 2);
            return (samples, channels);
        }
        catch
        {
            return ([], 0);
        }
    }
}
