# TM — palette-indexed texture

Palette-indexed textures with optional mip pyramids, wrapped in a generic recursive block container that also identifies itself with the magic `"BIFF"`. Every TM file is a BIFF file; only the block `TypeId` values differ from the prop-mesh BIFF format (see [biff-mesh.md](biff-mesh.md)).

## Purpose

TM stores the character/prop textures referenced by CIR models. Palette indirection compresses the data well (game textures reuse colour ramps heavily), and the mip pyramid — when present — gives the game runtime pre-filtered versions for distance rendering.

## Layout

Every TM file starts with a BIFF header:

```text
Header:
    char[4] Id             -- ASCII "BIFF"
    UInt32  Version        -- 1 or 2
    UInt32  Unknown1
    UInt32  Unknown2
    UInt32  NumBlocks
```

Followed by `NumBlocks` top-level blocks, each of which follows the same recursive block shape:

```text
Block:
    BlockHeader:
        UInt32 BeginMarker
        UInt32 TypeId
        UInt32 Unknown1
        Int32  DataSize
        UInt32 Unknown2         -- only present when file Version == 2

    <DataSize bytes of payload, interpreted per TypeId>

    BlockTrailer:
        UInt32 EndMarker
        UInt32 NumSubBlocks

    <NumSubBlocks nested Blocks, recursively>
```

**Important:** the decoder always seeks to `payloadStart + DataSize` before reading the trailer, regardless of how many bytes the payload handler actually consumed. Sub-block handlers may read less than the declared size; the container is still self-framing.

## Block types

Only two `TypeId` values carry meaningful data for a TM file:

| TypeId | Meaning |
| --- | --- |
| `0x02faf080` | Image block |
| `0x02faf082` | Palette block |

Every other `TypeId` is opaque: its `DataSize` bytes are skipped, but its `NumSubBlocks` are still walked (so palette state inside them still applies).

### Palette block (`0x02faf082`)

```text
UInt32 NumEntries
repeat NumEntries times:
    UInt16 R
    UInt16 G
    UInt16 B
```

Each channel is stored in a 16-bit field but **only the low byte is populated**. Do not right-shift — use the value as a plain 0–255 byte. Shifting right by 8 yields a black palette.

### Image block (`0x02faf080`)

```text
UInt16 NameLen              -- includes trailing NUL in the count
char[NameLen] Name          -- actual string is the first NameLen-1 characters
Byte   Unknown1
Int32  Width
Int32  Height
UInt32 NumLevels

repeat NumLevels times:
    byte[levelWidth * levelHeight]      -- one palette index per pixel
```

`levelWidth`/`levelHeight` start at `Width`/`Height` and halve (integer division, clamped to a minimum of 1) at each successive level.

## Semantics

- **Palette threading.** A single "current palette" is threaded by reference through the entire recursive block walk. Once a Palette block is seen, it applies to every subsequent Image block encountered anywhere later in the walk — nested sub-blocks *and* later siblings — until replaced by another Palette block. This is why palette-less image blocks aren't a decoder error: their palette was set by an earlier sibling.
- **Missing / out-of-range indices decode to black.** If a palette entry index is `>= palette.Length`, or no palette has been seen yet, the pixel decodes as `(0, 0, 0, 255)`.
- **Alpha is fully opaque.** TM has no transparency channel; the decoder writes `α = 255` for every pixel.
- **Mip layout when the caller asks for a mip preview.** When `useMipMap = true` and `NumLevels > 1`, the decoder composites all levels onto a single canvas: the base level fills `[0..Width) × [0..Height)`, and each subsequent level is stacked vertically in a strip of extra width `Width / 2` on the right. Callers rendering a texture at runtime pass `useMipMap = false` to get just level 0.

## Known unknowns

- `Unknown1` and `Unknown2` in the file header — 8 bytes total.
- `Unknown1` in every block header — 4 bytes.
- `Unknown2` in every block header — 4 bytes, **only present when the file version is 2**. This version gate is why the same block reader can't be reused unchanged between V1 and V2 files.
- `Unknown1` in the image block payload — 1 byte between the name and the width field.
- The `BeginMarker` / `EndMarker` fields are present in every block header/trailer but their values are not validated by the decoder.

## Decoder

- [`TmDecoder`](../../src/TLJExplorer.Core/Formats/TmDecoder.cs)
- Tests: [`TmDecoderTests`](../../tests/TLJExplorer.Core.Tests/TmDecoderTests.cs)
