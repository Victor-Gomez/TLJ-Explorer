using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class IsnDecoderTests
{
    [Fact]
    public void DecodeRawPcm_MonoTokens_ProduceExpectedWavHeader()
    {
        // "Sound" codec: tokens = "Sound", unknown1, sampleCount, channelsMinusOne, unknown2, divisorForFreq,
        // unknown3, unknown4, then raw 16-bit PCM payload to end-of-stream.
        byte[] payload = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00];
        byte[] input = BuildIsnFile(
            codec: "Sound",
            tokens: ["u1", "100", "0", "u2", "1", "u3", "u4"],
            payload: payload);

        using var output = new MemoryStream();
        IsnDecoder.DecodeToWav(new MemoryStream(input), output);

        // WAV header sanity: RIFF + WAVE + fmt + data
        byte[] outBytes = output.ToArray();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(outBytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(outBytes, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(outBytes, 12, 4));

        ushort channels = BitConverter.ToUInt16(outBytes, 22);
        uint sampleRate = BitConverter.ToUInt32(outBytes, 24);
        Assert.Equal(1, channels);
        Assert.Equal(44100u, sampleRate);

        // Payload appended verbatim after the 44-byte header.
        for (int i = 0; i < payload.Length; i++)
            Assert.Equal(payload[i], outBytes[44 + i]);
    }

    [Fact]
    public void DecodeRawPcm_StereoTokens_ProduceStereoWavHeader()
    {
        byte[] payload = new byte[16];
        byte[] input = BuildIsnFile(
            codec: "Sound",
            tokens: ["u1", "8", "1", "u2", "2", "u3", "u4"], // channels=1+1=2, divisor=2 → 22050 Hz
            payload: payload);

        using var output = new MemoryStream();
        IsnDecoder.DecodeToWav(new MemoryStream(input), output);

        byte[] outBytes = output.ToArray();
        Assert.Equal(2, BitConverter.ToUInt16(outBytes, 22));
        Assert.Equal(22050u, BitConverter.ToUInt32(outBytes, 24));
    }

    [Fact]
    public void DecodeRawPcm_UnrecognizedCodecToken_ThrowsFormatException()
    {
        // Codec is arbitrary text terminated by a space — feed something we don't recognize.
        var bytes = Encoding.ASCII.GetBytes("MP3_Sound 100 0 x 1 y z");
        var stream = new MemoryStream(bytes);
        using var output = new MemoryStream();

        Assert.Throws<FormatException>(() => IsnDecoder.DecodeToWav(stream, output));
    }

    private static byte[] BuildIsnFile(string codec, string[] tokens, byte[] payload)
    {
        var sb = new StringBuilder();
        sb.Append(codec).Append(' ');
        foreach (string t in tokens)
            sb.Append(t).Append(' ');

        byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
        var combined = new byte[header.Length + payload.Length];
        header.CopyTo(combined, 0);
        payload.CopyTo(combined, header.Length);
        return combined;
    }
}
