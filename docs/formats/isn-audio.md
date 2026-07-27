# ISN / ISS / SSN / SN — audio

The game's own audio format for sound effects, ambience, and dialogue. Depending on the archive, the same wire format appears with extensions `.isn`, `.iss`, `.ssn`, or `.sn` — they all decode the same way.

Ogg Vorbis clips live in `.ovs` files and are handled separately by [passthrough unwrap](passthrough.md).

## Purpose

Two codecs share this single wrapper: raw 16-bit PCM, and an ISN-specific IMA-ADPCM variant. In both cases the decoder emits a canonical PCM `.wav` file (standard RIFF header, `PCM` fmt chunk, 16-bit samples).

## Layout

The header is a run of whitespace-terminated ASCII tokens. Each token is read one byte at a time; a space (`0x20`) ends the token and is consumed. There is **no** newline, no leading count, no length prefix — just tokens separated by single spaces. A token longer than 255 characters, or end-of-stream before the terminator, means "this isn't ISN".

The first token identifies the codec:

| First token | Codec |
| --- | --- |
| `Sound` | Raw 16-bit little-endian PCM |
| `IMA_ADPCM_Sound` | ISN's IMA-ADPCM variant |

Every other value is an error.

### Raw PCM header

```text
Sound <Unknown1> <SampleCount> <ChannelsMinusOne> <Unknown2> <DivisorForFreq> <Unknown3> <Unknown4> <payload>
```

- `ChannelsMinusOne` — 0 for mono, 1 for stereo.
- `DivisorForFreq` — sample rate is `44100 / DivisorForFreq` Hz.
- `SampleCount`, `Unknown1..Unknown4` — read to advance the header cursor but not otherwise used. `SampleCount` in particular is not a byte count and the decoder does not rely on it; it streams the payload to end-of-input.

The payload is raw 16-bit little-endian PCM, interleaved by channel when stereo.

### IMA ADPCM header

```text
IMA_ADPCM_Sound <BlockSize> <Unknown1> <Unknown2> <ChannelsMinusOne> <Unknown3> <DivisorForFreq> <Unknown4> <Unknown5> <Size> <payload>
```

- `BlockSize` — length of one ADPCM block, in bytes.
- `Size` — total payload length, in bytes. Unlike the PCM case, this **is** used — the decoder reads exactly `Size` bytes and stops.

## IMA ADPCM block structure

Blocks are decoded per-channel using the standard IMA step and index tables (bundled in [`IsnDecoder`](../../src/TLJExplorer.Core/Formats/IsnDecoder.cs) — they match the canonical IMA tables). Each block begins with a per-channel preamble:

```text
Preamble (per channel, 4 bytes):
    Int16 InitialSample      -- emitted verbatim as sample-frame 0
    Byte  StepIndex
    Byte  Reserved
```

Total preamble is `4 * channels` bytes. The block's remaining bytes are 4-bit ADPCM codes, and it produces `sampPerBlock` frames total (including frame 0 from the preamble):

```text
sampPerBlock = ((BlockSize - preambleSize) * 8 / preambleSize) + 1
```

### The mono vs stereo interleave — this is the ISN quirk

Standard MS-IMA packs consecutive nibbles across channels in a fixed 4-byte cadence. **The ISN variant is different:**

- **Mono.** One byte carries two consecutive mono samples. Decode the low nibble first, emit a frame, then decode the high nibble, emit another frame. One byte → two frames.
- **Stereo.** One byte carries **one L/R frame**. The high nibble drives the left channel; the low nibble drives the right channel. One byte → one frame.

Get the interleave wrong and stereo output either desyncs channels or plays back at half speed. The mono case is easier to spot: mono ISN played back with MS-IMA rules sounds slower and lower-pitched.

### The `DecodeNibble` step

Standard IMA:

```text
step = StepTable[stepIndex]
diff = step >> 3
if (code & 4) diff += step
if (code & 2) diff += step >> 1
if (code & 1) diff += step >> 2
if (code & 8) predicted -= diff else predicted += diff
predicted = clamp(predicted, -32768, 32767)
stepIndex = clamp(stepIndex + IndexTable[code & 0x0F], 0, 88)
return predicted
```

`IndexTable[16] = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 }` — the standard IMA table.

The last (partial) block of a stream is common: `size / blockSize` may leave a remainder. If the remainder is larger than the preamble it is decoded as a shorter block with `partialSampPerBlock = ((remainderBytes - preambleSize) * 8 / preambleSize) + 1` frames.

## Output WAV

The decoder produces a canonical PCM `.wav`:

```text
RIFF | Chunk length | WAVE
"fmt " | 16 | format=1 (PCM) | channels | frequency | byteRate | blockAlign | 16
"data" | dataSize | <PCM samples>
```

`byteRate = frequency * channels * 2`, `blockAlign = channels * 2`. Samples are 16-bit little-endian, interleaved.

## Known unknowns

- Every `Unknown*` token in the header. All of them are consumed to advance the cursor but the decoder does not interpret them; the fields used above (channels, sample rate divisor, block size, size) are enough to produce a correct WAV.
- The `Reserved` byte at the end of each IMA preamble.

## Decoder

- [`IsnDecoder`](../../src/TLJExplorer.Core/Formats/IsnDecoder.cs)
- WAV playback is via the same output as `.ovs`/Ogg after decoding.
