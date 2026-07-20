# TLJ Explorer

A cross-platform viewer for the game assets of **The Longest Journey** (2000). Browse a TLJ install like a
file tree and preview its images, textures, sounds, videos, 3D models and animations, XRC resource
trees, and static composites of individual rooms.

![TLJ Explorer screenshot](docs/screenshot.avif)

## Features

- **Full-install browsing.** Reads `.xarc` archives recursively, grafts the friendly names and
  location hierarchy that live inside each archive's `.xrc` back into the tree.
- **Image preview.** Decodes `.xmg` (block-coded YUV/RGB with color-key transparency) and `.tm`
  (palette-indexed texture atlases, including mip levels).
- **Sound playback.** Plays `.isn`/`.iss`/`.ovs` clips — the ADPCM and Ogg Vorbis streams are decoded
  in-process to WAV so playback doesn't depend on system codecs.
- **Video preview.** Extracts `.sss` (Smacker) and `.bbb` (Bink) streams and transcodes them to MP4
  on demand via bundled ffmpeg. Optional cyan chroma-key removal for videos with alpha.
- **3D model viewer.** Renders `.cir` skeletal meshes with `.ani` animation playback (Silk.NET /
  OpenGL). Skin combo picks a `.tm` texture atlas per material.
- **Scene composite.** Click a room folder ("April's Room", etc.) and get a rendered still of the
  scene: backdrop plus every visible sprite and animated overlay at its authored position, mirroring
  the room as the player sees it on entry.
- **Structural dumps.** BIFF, XRC, CIR, and ANI records can be rendered as indented text trees for
  reverse-engineering and debugging.

## Building

Requires the **.NET 10 SDK**. Builds and runs on Windows and Linux (Avalonia UI).

```
dotnet build TLJExplorer.sln
dotnet run --project src/TLJExplorer
```

The scene viewer's animated overlays are extracted via ffmpeg, which the app expects to find at
`ffmpeg/ffmpeg.exe` (Windows) or `ffmpeg/ffmpeg` (Linux/macOS) next to the built binary. Drop a static
ffmpeg build there, install it via your platform's usual channel and point `AppSettings.FfmpegPath` at
it, or set the path from Options → Settings → External Tools.

Sound and video playback are backed by [LibVLC](https://www.videolan.org/vlc/libvlc.html). The Windows
build bundles it via NuGet; on Linux, install it from your distro's package manager (e.g.
`sudo apt install libvlc-dev vlc` on Debian/Ubuntu) since there's no redistributable NuGet package for
it on that platform.

## Project layout

- `src/TLJExplorer.Core/` — format decoders and virtual filesystem. Engine-agnostic; no UI framework, no GL.
  - `Formats/` — XMG, TM, XRC, CIR, ANI, ISN, Ogg, BIFF, hex-dump.
  - `FileSystem/` — `.xarc` archive reader plus the virtual tree built from it.
  - `Settings/` — persisted user preferences.
- `src/TLJExplorer/` — Avalonia UI.
  - `MainWindow.axaml` + code-behind — the browser, previewer, and scene viewer.
  - `Rendering/` — the OpenGL model renderer, skeleton posing, and scene compositor.
  - `Services/` — resource loading, ffmpeg driver, temp-file scratch dir, model catalog.
  - `ViewModels/` — the lazy tree-view wrapper around the virtual filesystem.

## Diagnostics

Toggle **Options → Dump Scene Diagnostics** to write a per-scene table (every item's subtype,
`enabled` flag, position, asset filename, and every item-enable script call) to
`TLJExplorer_last_scene_items.txt` in the OS temp folder each time a scene folder is opened. Useful
when a room renders the wrong sprite or picks a mid-animation frame.

## Status

The scene viewer is best-effort. It approximates "what the player sees on entry" by treating each
`Item`'s XRC `enabled` flag plus any item-enable command reachable from an on-enter or on-loop script
as visible. Scripts with conditional branches are always taken, so some scenes will show slightly more
than the game does at the moment you walk in.

## Acknowledgements

None of this would exist without the reverse-engineering work of two prior efforts:

- **[TLJView by Deniz Oezmen](https://oezmen.eu/)** — the original Delphi-based asset browser that
  worked out the shape of most of the container and image formats used here (XARC, XRC, XMG, TM,
  CIR, ANI, ISN). Every decoder in `TLJExplorer.Core/Formats/` builds directly on that foundation.
- **[ScummVM](https://www.scummvm.org/)** and its Stark engine — the open-source reimplementation
  of the TLJ engine. Their `engines/stark/` code was invaluable for understanding the XRC resource
  tree (Items, Anims, Scripts, Commands), the audio ADPCM variants, and the scene composition rules
  that the scene viewer here tries to approximate.

Thanks to both projects and everyone who has contributed to them.

The UI icons are from **[Lucide](https://lucide.dev)** (ISC licensed).
