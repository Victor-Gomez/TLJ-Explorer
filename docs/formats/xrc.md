# XRC — resource tree

Every `.xarc` archive contains a sibling `.xrc` file that describes the archive's contents as a recursive tree of typed records. It carries two independent kinds of information:

- **Structural** — location (folder) hierarchy, and friendly names + subtitles attached to sound/animation/dialogue entries. Consumed by the virtual filesystem to graft a readable directory tree onto the flat XARC name list.
- **Scene / behaviour** — everything else: items, images, animations, animation hierarchies, scripts, commands, camera setup, floor mesh, dialog tree, lights, lipsync, FMV triggers, PAT (path) tables, bookmarks, knowledge flags, scroll positions, texture sets. This is the actual "resource graph" ScummVM's Stark engine walks to run a scene.

The two are read by different components in this repo — a lightweight structural reader for the VFS graft, and a much larger display dumper for human inspection.

## Purpose

XRC is the game's serialised resource graph. In ScummVM's Stark engine (`engines/stark/resources/*.cpp`), each `TypeId` corresponds to a `Resource` subclass; on disk that class's `readData` method defines the payload layout. Since Stark is the canonical decoder, the type IDs and payload interpretations documented here match Stark's `Object::Type::ResourceType` values in `engines/stark/resources/object.h`.

## Record layout

Every record has the same wire format:

```text
TXRCRecord:
    Byte    TypeId
    Byte    SubType        -- variant selector inside a TypeId (Stark calls this "SubType")
                           -- structural reader calls it "Tag1"; same field
    UInt16  Tag2            -- ID/handle; meaning depends on TypeId
    UInt16  NameLen
    char[NameLen] Name      -- Latin-1, exact character count, no null terminator
    Int32   DataSize
    byte[DataSize] Data     -- per-(TypeId, SubType) payload
    UInt16  NumChildren
    UInt16  Unknown3        -- expected to be 0; nonzero indicates a structural anomaly

<NumChildren TXRCRecord children follow immediately, recursively>
```

The stream is a sequence of one or more sibling root records — the structural reader treats the first as *the* root and continues reading further siblings until end-of-stream.

## Primitive conventions inside `Data`

Field layouts inside the `Data` blob follow ScummVM's `XRCReadStream` conventions:

| Type in Stark | Wire format |
| --- | --- |
| `bool` | `UInt32` — nonzero means true |
| `Point` | Two `UInt32`s |
| `Rect` | Four `Int32`s — left, top, right, bottom |
| `Vector3` | Three `Single`s |
| `String` | `UInt16 Length` followed by that many Latin-1 bytes |

`UInt16` string lengths inside `Data` are relative offsets into the record's payload buffer, not the file stream.

## Type IDs

Per-record payload layouts for every documented type live in the companion [xrc-records.md](xrc-records.md). This page only covers the outer record shape and which type IDs the structural reader cares about.

Structural reader ([`XrcStructureReader`](../../src/TLJExplorer.Core/FileSystem/XrcStructureReader.cs)) inspects only:

| TypeId | Meaning | What the reader extracts |
| --- | --- | --- |
| `0x02` | Location (variant A) | Adds an `XrcLocation(Id = Tag2, Name)` |
| `0x03` | Location (variant B) | Same as `0x02` |
| `0x0B` | Animation reference | If the referenced file ends `.sss`/`.bbb` (Smacker/Bink FMV), attach the record's `Name` as the FMV's friendly name |
| `0x10` | Sound reference | Attach the record's `Name` as the sound's friendly name |
| `0x1D` | Dialogue (speech) | Locate the child `Sound` (0x10), attach `Name` + subtitle string as extended info |

Display dumper ([`XrcDisplayDump`](../../src/TLJExplorer.Core/Formats/XrcDisplayDump.cs)) additionally decodes:

| TypeId | Name | TypeId | Name |
| --- | --- | --- | --- |
| `0x04` | Layer | `0x16` | Command |
| `0x05` | Camera | `0x17` | PAT (path) table |
| `0x06` | Floor | `0x1B` | Dialog |
| `0x07` | FloorFace | `0x1D` | Speech |
| `0x08` | Item | `0x1E` | Light |
| `0x09` | Script | `0x20` | BonesMesh |
| `0x0A` | AnimHierarchy | `0x21` | Scroll |
| `0x0B` | Anim | `0x22` | FMV |
| `0x0C` | Direction | `0x23` | Lipsync |
| `0x0D` | Image | `0x24` | AnimSoundTrigger |
| `0x0F` | AnimScriptItem | `0x26` | TextureSet |
| `0x11` | Path | `0x12` | FloorField |
| `0x13` | Bookmark | `0x15` | Knowledge |

Type IDs not listed above appear in some files but are dumped as `type=0xNN` with the raw data as a hex dump.

## Semantics

- **The two readers are intentionally distinct.** `XrcStructureReader` is a tight, allocation-light reader that only understands enough type IDs to graft directory names and file annotations onto the VFS. `XrcDisplayDump` is a much bigger, human-readable dumper used by the UI's XRC viewer. Do not share code between them — one is on the hot path of archive loading, the other is not.
- **Encoding is Latin-1.** Names use extended Latin characters in localised builds; treating the byte stream as ASCII would drop accented letters.
- **`Unknown3` is a canary.** The 16-bit tail field on every record is expected to be zero. Real files honour that. If a decoder produces garbage records it is almost always because a preceding record's `DataSize` was off and the reader is now mis-framed; a nonzero `Unknown3` is the first place that shows up. `XrcDisplayDump` surfaces it as a warning line; `XrcStructureReader` just consumes it and keeps going.
- **Scene composition is approximate.** The scene viewer's rules for "which items are visible on entry" are documented in the top-level [`CLAUDE.md`](../../CLAUDE.md) — scripts are always taken conditionally, so scenes may show slightly more than the game does.

## Known unknowns

- **`Unknown3`** — the trailing 16 bits are always zero in valid files; if there's a semantic use, it's undocumented.
- Many individual record subtypes carry `Unknown*` fields inside `Data` — see the per-record code paths in `XrcDisplayDump` for the complete list.

## Decoders

- Structural: [`XrcStructureReader`](../../src/TLJExplorer.Core/FileSystem/XrcStructureReader.cs)
- Human-readable dump: [`XrcDisplayDump`](../../src/TLJExplorer.Core/Formats/XrcDisplayDump.cs)
- Scene interpretation: [`XrcSceneModel`](../../src/TLJExplorer.Core/Formats/XrcSceneModel.cs) — walks the resource tree into a rendered scene composite.
