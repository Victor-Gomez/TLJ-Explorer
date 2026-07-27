# TLJ file format reference

Notes on the reverse-engineered file formats used by *The Longest Journey* (2000), as implemented by the decoders under [`src/TLJExplorer.Core/`](../../src/TLJExplorer.Core/). These are working notes — they describe what the decoders in this repository actually parse; there is no official specification.

Everything below is little-endian unless stated otherwise. Bit-widths (`UInt32`, `Int16`, `Single`, …) match .NET `System.IO.BinaryReader`. `Single` = IEEE-754 32-bit float.

## Prior art

Two projects did the original reverse engineering; the decoders here are cross-checked against them and referenced by comment when a field's meaning is non-obvious:

- **TLJView** by Deniz Oezmen — original Delphi tool. Container/image/mesh/animation shapes for XARC, XRC, XMG, TM, CIR, ANI, ISN.
- **ScummVM's Stark engine** (`engines/stark/`) — reference for the XRC record tree semantics (`Items`, `Anims`, `Scripts`, `Commands`), the audio ADPCM variants, BIFF prop meshes, and scene composition. Type IDs in the XRC documentation here come straight from `engines/stark/resources/object.h`.

When one of the decoders does something surprising, one of these two is usually the reason.

## Format index

| File / extension | Kind | Doc |
| --- | --- | --- |
| `.xarc` | Container archive holding all game data | [xarc.md](xarc.md) |
| `.xrc` (inside each `.xarc`) | Recursive resource tree — location hierarchy, items, scripts, dialogue, animation graph | [xrc.md](xrc.md) — outer shape · [xrc-records.md](xrc-records.md) — per-record payloads |
| `.xmg` | Full-screen background image, RLE over 2×2 blocks (YUV / void / RGB) | [xmg.md](xmg.md) |
| `.tm` | Palette-indexed texture, mip levels, wrapped in a BIFF block container | [tm.md](tm.md) |
| `.cir` | 3D skeletal character model — materials + bones + dual-position skinned vertices | [cir.md](cir.md) |
| `.ani` | Skeletal animation — per-bone quaternion+position keyframes | [ani.md](ani.md) |
| BIFF prop mesh | Per-keyframe prop mesh embedded in `.tm`-flavoured BIFF container | [biff-mesh.md](biff-mesh.md) |
| `.isn` / `.iss` / `.ssn` / `.sn` | Audio — raw PCM or an ISN-specific IMA-ADPCM variant, text-then-binary header | [isn-audio.md](isn-audio.md) |
| `.ovs` / `.sss` / `.bbb` | Passthrough containers — verbatim Ogg Vorbis / Smacker / Bink under TLJ-specific extensions | [passthrough.md](passthrough.md) |

## Reading these docs

Every doc has the same shape:

1. **Purpose** — what the format holds, where it appears in a TLJ install.
2. **Layout** — the on-disk byte layout, in reader order. Fields are presented as pseudocode with C-like types.
3. **Semantics** — how to interpret the bytes: coordinate systems, opcode dispatch, ordering quirks, colour-key rules, blending rules — whatever is non-obvious once you can already read the bytes.
4. **Known unknowns** — fields the decoder skips because their meaning isn't understood. Kept explicit so readers know which parts of the format are guessed vs. verified.
5. **Decoder** — a link to the file that implements it, and to its tests.

## What the decoders share

A few conventions repeat across most formats:

- **Length-prefixed strings** — `UInt32 Length` (or `UInt16 NameLen`) followed by exactly that many raw bytes, no NUL terminator. XARC entry names are the odd one out — those *are* null-terminated ASCII.
- **Length-prefixed arrays** — `Int32 Count` followed by that many entries. Used everywhere in CIR/ANI.
- **The `0xDEADBABE` sentinel** — CIR and ANI both include this magic word after the version-dependent preamble. If the decoder sees anything else, the stream is misaligned and everything downstream is garbage — the decoders throw immediately rather than produce silently-wrong output.
- **"BIFF" as a block container** — both `.tm` texture files and `.biff` prop meshes use the same generic recursive block format described in [tm.md](tm.md); only the `TypeId` values inside differ.
- **The "Unknown*" fields** — many records carry fields whose meaning isn't understood. The decoders read them (to stay aligned) and discard them. Every doc lists what they are so it's obvious what still needs figuring out.
