# XARC — archive container

Every piece of game data — meshes, textures, animations, audio, scripts — lives inside a `.xarc` file. A TLJ install is a tree of directories, each of which may carry a `<dirname>.xarc` archive alongside loose files.

## Purpose

XARC is a simple concatenated-blob archive with a directory of `(name, size, locale-flag)` entries. Entry data starts at a base offset given in the header and is laid out end-to-end with no per-entry offset table — offsets are recomputed cumulatively at read time.

## Layout

```text
Header:
    Int32   Unknown
    Int32   NumFiles
    Int32   BaseOfs        -- absolute byte offset of the first entry's data

repeat NumFiles times:
    Name        -- null-terminated ASCII, max 256 bytes (including NUL)
    Int32   Size
    UInt32  LocaleFlag     -- 0 = English, 1 = localised

<blob data, back-to-back, starting at BaseOfs>
```

Entry `i`'s data lives at `Offset[i] = BaseOfs + sum(Size[0..i-1])`. There is no per-entry offset stored on disk.

## Semantics

- **Entry names are file names, not paths.** Any directory structure comes from the sibling `.xrc` file inside the archive (see [xrc.md](xrc.md)), which grafts a "locations" hierarchy on top of the flat name list.
- **`LocaleFlag == 1` means the entry is a localised variant** (subtitles/speech in a non-English language). The virtual filesystem picks between English and localised copies based on user preference; the raw flag is preserved on the entry so tooling can filter on it.
- **Non-recognised locale flag values are surfaced unchanged.** The decoder tracks the raw `UInt32` in `XarcEntry.LocaleFlagRaw` alongside the boolean `IsLocalized`, so an unexpected value isn't silently squashed to 0/1.
- **Name terminator safety limit.** The decoder aborts if a name isn't null-terminated within 256 bytes — a runaway name is the first thing that goes wrong on a corrupt or misidentified file, and this limit stops the reader from wandering off through the entry table.

## Known unknowns

- The first `Int32` of the header ("Unknown") is not interpreted. In practice it varies across archives; no consistent meaning has been derived.

## Decoder

- Reader: [`XarcArchive`](../../src/TLJExplorer.Core/FileSystem/XarcArchive.cs)
- Streams over a single entry: [`ArchiveWindowStream`](../../src/TLJExplorer.Core/FileSystem/ArchiveWindowStream.cs) — a bounded view into the archive without loading the whole thing into memory.
- Virtual FS built on top: [`VirtualFileSystem`](../../src/TLJExplorer.Core/FileSystem/VirtualFileSystem.cs)
