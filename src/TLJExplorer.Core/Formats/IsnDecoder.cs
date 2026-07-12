using System.Globalization;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Decodes the "ISN" family of TLJ sound files (also seen with extensions such as
/// <c>.iss</c>, <c>.ssn</c>, <c>.sn</c>) into canonical PCM WAV data.
/// </summary>
/// <remarks>
/// <para>
/// The format is a reverse-engineered, text-then-binary hybrid layout:
/// </para>
/// <para>
/// <b>Header.</b> A sequence of whitespace-terminated ASCII tokens. Each token is read
/// one character at a time until a space (0x20) byte is encountered (the space is
/// consumed as the terminator and is not part of the token), or until 255 characters
/// have accumulated without hitting a space, or end-of-stream is reached first --
/// either of the latter two conditions indicates malformed/non-ISN input and results
/// in a <see cref="FormatException"/>.
/// </para>
/// <para>
/// The first token identifies the codec:
/// <list type="bullet">
/// <item><description><c>"Sound"</c> -- raw 16-bit PCM payload follows.</description></item>
/// <item><description><c>"IMA_ADPCM_Sound"</c> -- IMA ADPCM-encoded payload follows.</description></item>
/// </list>
/// Any other value is an unrecognized codec and is rejected.
/// </para>
/// <para>
/// <b>Payload.</b> Immediately after the header's last token (and its terminating
/// space) comes the raw binary audio payload, whose length is given by a <c>Size</c>
/// field in the header. All multi-byte binary values in the payload are little-endian.
/// </para>
/// </remarks>
public static class IsnDecoder
{
    private const int MaxTokenLength = 255;
    private const byte TokenTerminator = 0x20;

    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282,
        1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845,
        8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>
    /// Decodes an ISN/ISS/SSN/SN sound stream and writes a canonical PCM WAV file to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">The ISN source stream, positioned at the very start of the file.</param>
    /// <param name="output">The destination stream the WAV file is written to.</param>
    /// <exception cref="FormatException">
    /// The header could not be tokenized (garbage/non-ISN input), or the codec name is unrecognized.
    /// </exception>
    public static void DecodeToWav(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var codec = ReadToken(input);

        switch (codec)
        {
            case "Sound":
                DecodeRawPcm(input, output);
                break;
            case "IMA_ADPCM_Sound":
                DecodeImaAdpcm(input, output);
                break;
            default:
                throw new FormatException($"Unrecognized ISN codec token: \"{codec}\".");
        }
    }

    /// <summary>
    /// Reads one whitespace-terminated ASCII token from <paramref name="stream"/>.
    /// The terminating space is consumed but not included in the returned token.
    /// </summary>
    private static string ReadToken(Stream stream)
    {
        var buffer = new byte[MaxTokenLength];
        var length = 0;

        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new FormatException("Unexpected end of stream while reading an ISN header token.");
            }

            if (b == TokenTerminator)
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }

            if (length == MaxTokenLength)
            {
                throw new FormatException("ISN header token exceeded the maximum length of 255 characters without a terminating space; input is likely not a valid ISN stream.");
            }

            buffer[length++] = (byte)b;
        }
    }

    private static int ReadIntToken(Stream stream) =>
        int.Parse(ReadToken(stream), CultureInfo.InvariantCulture);

    private static void DecodeRawPcm(Stream input, Stream output)
    {
        // The header's "size" field is a sample count, not a byte count, and is not needed to bound
        // reads. We stream to end-of-input instead.
        _ = ReadToken(input); // Unknown1
        _ = ReadIntToken(input); // sample count, ignored
        var channelsMinusOne = ReadIntToken(input);
        _ = ReadToken(input); // Unknown2
        var divisorForFreq = ReadIntToken(input);
        _ = ReadToken(input); // Unknown3
        _ = ReadToken(input); // Unknown4

        var channels = channelsMinusOne + 1;
        var frequency = 44100 / divisorForFreq;

        using var payload = new MemoryStream();
        input.CopyTo(payload);
        var payloadBytes = payload.ToArray();

        WriteWavHeader(output, channels, frequency, payloadBytes.Length);
        output.Write(payloadBytes, 0, payloadBytes.Length);
    }

    private static void DecodeImaAdpcm(Stream input, Stream output)
    {
        // Only fields actually used below are parsed as integers (see DecodeRawPcm).
        var blockSize = ReadIntToken(input);
        _ = ReadToken(input); // Unknown1
        _ = ReadToken(input); // Unknown2
        var channelsMinusOne = ReadIntToken(input);
        _ = ReadToken(input); // Unknown3
        var divisorForFreq = ReadIntToken(input);
        _ = ReadToken(input); // Unknown4
        _ = ReadToken(input); // Unknown5
        var size = ReadIntToken(input);

        var channels = channelsMinusOne + 1;
        var frequency = 44100 / divisorForFreq;

        var preambleSize = 4 * channels;
        var sampPerBlock = (((blockSize - preambleSize) * 8) / preambleSize) + 1;

        var fullBlocks = size / blockSize;
        var remainderBytes = size % blockSize;

        var numSamples = fullBlocks * sampPerBlock;
        var partialSampPerBlock = 0;
        if (remainderBytes > preambleSize)
        {
            partialSampPerBlock = (((remainderBytes - preambleSize) * 8) / preambleSize) + 1;
            numSamples += partialSampPerBlock;
        }

        var subchunk2Size = numSamples * channels * 2;
        WriteWavHeader(output, channels, frequency, subchunk2Size);

        var payload = new byte[size];
        ReadExactly(input, payload, size);

        var predictedSample = new int[channels];
        var stepIndex = new int[channels];
        var sampleBuffer = new short[channels];

        var offset = 0;
        for (var block = 0; block < fullBlocks; block++)
        {
            DecodeBlock(payload, offset, blockSize, channels, sampPerBlock, predictedSample, stepIndex, sampleBuffer, output);
            offset += blockSize;
        }

        if (remainderBytes > preambleSize)
        {
            DecodeBlock(payload, offset, remainderBytes, channels, partialSampPerBlock, predictedSample, stepIndex, sampleBuffer, output);
        }
    }

    /// <summary>
    /// Decodes a single IMA ADPCM block (which may be the final, shorter block of the
    /// stream) and writes <paramref name="sampPerBlock"/> interleaved sample-frames of
    /// 16-bit PCM to <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// The ISS/ISN variant differs from MS-IMA: after the per-channel 4-byte preamble,
    /// the encoder reads ONE byte per iteration and splits its two nibbles across the
    /// channels (low nibble → right, high nibble → left for stereo; low then high for mono).
    /// </remarks>
    private static void DecodeBlock(
        byte[] payload,
        int blockOffset,
        int blockSize,
        int channels,
        int sampPerBlock,
        int[] predictedSample,
        int[] stepIndex,
        short[] sampleBuffer,
        Stream output)
    {
        var preambleSize = 4 * channels;

        // Per-channel 4-byte preamble: Int16 InitialSample, Byte StepIndex, Byte Reserved.
        for (var ch = 0; ch < channels; ch++)
        {
            var p = blockOffset + (ch * 4);
            var initialSample = (short)(payload[p] | (payload[p + 1] << 8));
            var stIndex = payload[p + 2];

            predictedSample[ch] = initialSample;
            stepIndex[ch] = stIndex;
        }

        // Emit the initial sample-frame (frame 0), reconstructed from the preamble.
        for (var ch = 0; ch < channels; ch++)
            sampleBuffer[ch] = (short)predictedSample[ch];
        WriteSampleFrame(output, sampleBuffer, channels);

        var pos = blockOffset + preambleSize;
        var blockEnd = blockOffset + blockSize;
        var framesRemaining = sampPerBlock - 1;

        if (channels == 1)
        {
            // One byte carries two mono samples (low nibble first, then high nibble),
            // so each byte advances the output by two frames.
            while (framesRemaining > 0 && pos < blockEnd)
            {
                var b = payload[pos++];

                var lo = DecodeNibble(b & 0x0F, ref predictedSample[0], ref stepIndex[0]);
                sampleBuffer[0] = (short)lo;
                WriteSampleFrame(output, sampleBuffer, 1);
                framesRemaining--;

                if (framesRemaining <= 0)
                    break;

                var hi = DecodeNibble((b >> 4) & 0x0F, ref predictedSample[0], ref stepIndex[0]);
                sampleBuffer[0] = (short)hi;
                WriteSampleFrame(output, sampleBuffer, 1);
                framesRemaining--;
            }
        }
        else
        {
            // Stereo: one byte carries one L/R frame. High nibble drives the left channel
            // (channel 0), low nibble drives the right channel (channel 1).
            while (framesRemaining > 0 && pos < blockEnd)
            {
                var b = payload[pos++];

                var right = DecodeNibble(b & 0x0F, ref predictedSample[1], ref stepIndex[1]);
                var left = DecodeNibble((b >> 4) & 0x0F, ref predictedSample[0], ref stepIndex[0]);

                sampleBuffer[0] = (short)left;
                sampleBuffer[1] = (short)right;
                WriteSampleFrame(output, sampleBuffer, 2);
                framesRemaining--;
            }
        }
    }

    private static int DecodeNibble(int code, ref int predictedSample, ref int stepIndex)
    {
        var step = StepTable[stepIndex];
        var diff = step >> 3;

        if ((code & 4) != 0)
        {
            diff += step;
        }

        if ((code & 2) != 0)
        {
            diff += step >> 1;
        }

        if ((code & 1) != 0)
        {
            diff += step >> 2;
        }

        if ((code & 8) != 0)
        {
            predictedSample -= diff;
        }
        else
        {
            predictedSample += diff;
        }

        predictedSample = Math.Clamp(predictedSample, -32768, 32767);

        stepIndex = Math.Clamp(stepIndex + IndexTable[code & 0x0F], 0, 88);

        return predictedSample;
    }

    private static void WriteSampleFrame(Stream output, short[] sampleBuffer, int channels)
    {
        Span<byte> frame = stackalloc byte[channels * 2];
        for (var ch = 0; ch < channels; ch++)
        {
            var s = sampleBuffer[ch];
            frame[ch * 2] = (byte)(s & 0xFF);
            frame[(ch * 2) + 1] = (byte)((s >> 8) & 0xFF);
        }

        output.Write(frame);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading ISN payload data.");
            }

            totalRead += read;
        }
    }

    private static void WriteWavHeader(Stream output, int channels, int frequency, int dataSize)
    {
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        var byteRate = frequency * channels * 2;
        var blockAlign = (ushort)(channels * 2);

        writer.Write("RIFF"u8.ToArray());
        writer.Write((uint)(36 + dataSize));
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write((uint)16);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)channels);
        writer.Write((uint)frequency);
        writer.Write((uint)byteRate);
        writer.Write(blockAlign);
        writer.Write((ushort)16); // bits per sample
        writer.Write("data"u8.ToArray());
        writer.Write((uint)dataSize);
    }
}
