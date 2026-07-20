using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

public class WaveformRendererTests
{
    /// <summary>Writes a minimal canonical 16-bit PCM WAV file with the given mono samples.</summary>
    private static string WriteTestWav(short[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tlj-waveform-test-{Guid.NewGuid():N}.wav");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        int dataSize = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(44100); // sample rate
        writer.Write(44100 * 2); // byte rate
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits per sample

        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (short s in samples)
            writer.Write(s);

        return path;
    }

    [Fact]
    public void SamplePeaks_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(WaveformRenderer.SamplePeaks(Path.Combine(Path.GetTempPath(), "does-not-exist.wav")));
    }

    [Fact]
    public void SamplePeaks_ZeroBarCount_ReturnsEmpty()
    {
        string path = WriteTestWav([0, 0, 0, 0]);
        try
        {
            Assert.Empty(WaveformRenderer.SamplePeaks(path, barCount: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SamplePeaks_Silence_ProducesNearZeroPeaks()
    {
        string path = WriteTestWav(new short[200]);
        try
        {
            float[] peaks = WaveformRenderer.SamplePeaks(path, barCount: 10);

            Assert.NotEmpty(peaks);
            Assert.All(peaks, p => Assert.Equal(0f, p));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SamplePeaks_FullScaleSample_ProducesPeakNearOne()
    {
        short[] samples = new short[100];
        samples[50] = short.MaxValue;
        string path = WriteTestWav(samples);
        try
        {
            float[] peaks = WaveformRenderer.SamplePeaks(path, barCount: 100);

            Assert.Contains(peaks, p => p > 0.99f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SamplePeaks_NotAWavFile_ReturnsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tlj-waveform-test-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        try
        {
            Assert.Empty(WaveformRenderer.SamplePeaks(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Render_EmptyPeaks_ReturnsNull()
    {
        Assert.Null(WaveformRenderer.Render([], Colors.White));
    }

    [AvaloniaFact]
    public void Render_ZeroCanvasSize_ReturnsNull()
    {
        Assert.Null(WaveformRenderer.Render([0.5f], Colors.White, canvasWidth: 0));
    }

    [AvaloniaFact]
    public void Render_ValidPeaks_ReturnsImage()
    {
        float[] peaks = Enumerable.Repeat(0.5f, 64).ToArray();

        IImage? image = WaveformRenderer.Render(peaks, Colors.White, canvasWidth: 400, canvasHeight: 48);

        Assert.NotNull(image);
        Assert.IsType<DrawingImage>(image);
    }

    [AvaloniaFact]
    public void Render_RequestingFewerBarsThanPeaks_StillReturnsImage()
    {
        float[] peaks = Enumerable.Range(0, 1024).Select(i => i / 1024f).ToArray();

        IImage? image = WaveformRenderer.Render(peaks, Colors.White, canvasWidth: 400, canvasHeight: 48, barCount: 20);

        Assert.NotNull(image);
    }
}
