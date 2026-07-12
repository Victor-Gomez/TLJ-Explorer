using NVorbis;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Decodes an Ogg Vorbis stream (the payload wrapped inside TLJ's <c>.ovs</c> sound files, unwrapped
/// verbatim by <see cref="ContainerUnwrap"/>) to a canonical 16-bit PCM WAV file.
/// </summary>
/// <remarks>
/// WPF's built-in <c>MediaPlayer</c> only plays whatever formats the host OS's installed Media
/// Foundation/DirectShow codecs support, and Ogg Vorbis is not one of them on a typical Windows
/// install. Rather than depend on that, we decode Vorbis ourselves with the pure-managed NVorbis
/// library and hand the UI a WAV file instead -- WAV playback works everywhere with no extra
/// system codecs required.
/// </remarks>
public static class OggDecoder
{
    public static void DecodeToWav(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        using var reader = new VorbisReader(input, closeOnDispose: false);

        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;

        using var pcm = new MemoryStream();
        Span<float> buffer = stackalloc float[4096];

        int samplesRead;
        while ((samplesRead = reader.ReadSamples(buffer)) > 0)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                short sample = (short)Math.Clamp(
                    (int)Math.Round(buffer[i] * short.MaxValue), short.MinValue, short.MaxValue);
                pcm.WriteByte((byte)(sample & 0xFF));
                pcm.WriteByte((byte)((sample >> 8) & 0xFF));
            }
        }

        WriteWavHeader(output, channels, sampleRate, (int)pcm.Length);
        pcm.Position = 0;
        pcm.CopyTo(output);
    }

    private static void WriteWavHeader(Stream output, int channels, int sampleRate, int dataSize)
    {
        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16); // PCM fmt chunk size
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2); // byte rate
        writer.Write((short)(channels * 2)); // block align
        writer.Write((short)16); // bits per sample

        writer.Write("data"u8);
        writer.Write(dataSize);
    }
}
