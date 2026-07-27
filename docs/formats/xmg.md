# XMG — background image

Full-screen background images, cutout sprites, and cursor art. Stored as a run-length-encoded stream of opcodes that operate on **2×2 pixel blocks**, using three encodings interchangeably: a YUV-like luma + shared chroma, a transparent "void" fill, or raw RGB triples.

## Purpose

XMG's block-level RLE trades a tiny bit of decode complexity for very good compression on painted backgrounds — large flat areas and gentle gradients compress into short YUV runs, cutout transparency compresses into short Void runs, and only genuinely sharp / high-frequency detail (sprite edges, fine lettering) pays the full RGB cost.

## Layout

```text
Header:
    UInt32  Version           -- must be 3
    Byte    VoidR
    Byte    VoidG
    Byte    VoidB             -- the "transparent" background colour
    Byte    Empty             -- padding, ignored
    UInt32  Width
    UInt32  Height
    UInt32  LineLen           -- must equal Width * 3
    UInt32  Unknown3
    UInt32  Unknown4

<opcode stream>
```

The opcode stream terminates on either a `0xFF` byte or end-of-stream — some files omit the sentinel.

## Opcode dispatch

Every opcode encodes a run of N identically-typed 2×2 blocks. The block cursor `(px, py)` starts at `(0, 0)`; after each block, `px += 2` and if `px >= Width` it wraps to `px = 0, py += 2`.

For **odd `Width`**, this precomputes to nothing sensible — you cannot loop over `Width/2` blocks per row. You must track `(px, py)` explicitly and check the wrap condition on every advance. Odd widths are common for small sprites.

Opcode dispatch:

| Byte range | Mode | Run length | Extra bytes per block |
| --- | --- | --- | --- |
| `0x00`–`0x3F` | **YUV** | `opcode` | 6 bytes: `Y0, Y1, Y2, Y3, Cr, Cb` |
| `0x40`–`0x7F` | **Void** | `opcode & 0x3F` | none — block fills with `(VoidR, VoidG, VoidB, α=0)` |
| `0x80`–`0xBF` | **RGB** | `opcode & 0x3F` | 12 bytes: 4× `(R, G, B)` |
| `0xC0`–`0xFE` | Extended | see below | as base mode |
| `0xFF` | End of stream | — | — |

For extended opcodes (`0xC0`–`0xFE`), decode as:

```text
mode       = (opcode >> 4) & 0x3        -- 0 = YUV, 1 = Void, 2 = RGB
secondByte = <next byte in stream>
runLength  = ((opcode & 0x0F) << 8) + secondByte
```

Values with `mode == 3` are invalid and should be rejected.

## Block payloads

**YUV block.** Six bytes per block: `Y0 Y1 Y2 Y3 Cr Cb`. `Y0`..`Y3` are the four sub-pixel lumas in reading order (top-left, top-right, bottom-left, bottom-right). `Cr` and `Cb` are shared across all four sub-pixels and are stored as unsigned bytes biased by 128 (i.e. subtract 128 to get the signed chroma).

Conversion to 8-bit RGB uses standard ITU-R BT.601 coefficients:

```text
R = Y + 1.402   * Cr
G = Y - 0.344136 * Cb - 0.714136 * Cr
B = Y + 1.772   * Cb
```

In this codebase the coefficients are kept in 16.16 fixed point and rounded with a `+32768` bias before `>> 16`, so the arithmetic stays in `Int32`. See [`XmgDecoder.WriteYuvPixel`](../../src/TLJExplorer.Core/Formats/XmgDecoder.cs) — a full-screen background touches millions of sub-pixels and the double-precision path is a measurable cost.

**Void block.** No bytes consumed. All four sub-pixels are written as `(VoidR, VoidG, VoidB, α=0)`.

**RGB block.** Twelve bytes per block: four `(R, G, B)` triples in the same top-left / top-right / bottom-left / bottom-right order.

## Colour-key transparency

After decoding, a whole-image pass runs one more time over the pixel buffer: **any pixel whose RGB exactly matches `(VoidR, VoidG, VoidB)` is forced to α = 0**, regardless of whether it was written by a Void block or by a YUV/RGB block that happened to land on the void colour.

This is not cosmetic — YUV blocks at the edge of a cutout sprite frequently decode to the void colour by rounding, and if the pass is skipped they show up as a visible fringe around every foreground element. The [`XmgDecoder`](../../src/TLJExplorer.Core/Formats/XmgDecoder.cs) does this in `ApplyColorKeyTransparency`.

The transparent colour is also carried on the returned `DecodedImage` as `TransparentColorBgr` so exporters (PNG, TGA) can encode it explicitly.

## Edge clipping for odd dimensions

For odd `Width` or `Height`, the second column/row of the last 2×2 block in a row/column falls one pixel past the image edge. The original code silently clips this write; the decoder here does the same via a bounds check in `WritePixel`.

## Known unknowns

- `Unknown3` and `Unknown4` in the header — 8 bytes total, present in every file, no known interpretation.
- `Empty` byte after the void colour — assumed to be padding.

## Decoder

- [`XmgDecoder`](../../src/TLJExplorer.Core/Formats/XmgDecoder.cs)
- Tests: [`XmgDecoderTests`](../../tests/TLJExplorer.Core.Tests/XmgDecoderTests.cs)
