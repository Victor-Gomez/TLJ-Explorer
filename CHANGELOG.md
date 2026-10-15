# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.6] - 2026-10

### Added
- **Image Comparison**: Added Onion Skin and Difference comparison modes alongside Wipe and Side-by-side.
  - **Onion Skin**: Stack original and modded images with an adjustable opacity slider (0% to 100%).
  - **Difference**: Pixel-by-pixel absolute difference visualization highlighting altered pixels, with an amplification gain slider (1x–10x) and differing pixel count statistics.

## [1.2.5] - 2026-10

### Added
- **Continuous Integration**: Added automated multi-platform CI workflow for pushes, PRs, and scheduled health checks.
- **Automated Dependency Updates**: Enabled Dependabot for monthly NuGet and GitHub Actions maintenance.
- **Documentation & Packaging**: Added direct pre-built binary download instructions, status badges, and MIT license.

### Changed
- Updated Avalonia to `11.3.22`, LibVLCSharp to `3.10.2`, VideoLAN.LibVLC.Windows to `3.0.24`, xunit to `2.9.3`, and test SDKs to `17.14.1`.

## [1.2.4] - 2026-10

### Changed
- Improved automatic model and texture matching when previewing animations.

## [1.2.3] - 2026-09

### Fixed
- Fixed scene preview context actions unit tests and packaging polish.

## [1.2.2] - 2026-09

### Changed
- Migrated drag-and-drop file operations to Avalonia 11.3 `DataTransfer` API.

## [1.2.1] - 2026-09

### Fixed
- Fixed startup launch crash on clean installs (issue #1).

## [1.2.0] - 2026-08

### Added
- **Update Checks**: Check for updates via Help menu and optional startup check (disabled by default until consented).
- **Diagnostics**: Help → About window with commit SHA, LibVLC/ffmpeg resolution status, and "Copy Diagnostics" button.
- **Unified Logging**: Consolidated all runtime logging and crash handlers to a single rotated log (`%LOCALAPPDATA%/TLJExplorer/log.txt`).
- **Window Geometry Persistence**: Restores window dimensions, position, and maximized state with disconnected-monitor safety fallbacks.
- **Shortcuts & Navigation**: `F1` shortcut cheat sheet; `Ctrl+=`, `Ctrl+-`, and `Ctrl+0` zoom controls in image and scene previews.
- **Context Menus**: Right-click actions on tree nodes and preview panes.
- **Drag & Drop**: Drop an install folder to open; drag files out to export.
- **Reverse-Engineering Docs**: Added comprehensive format specifications in `docs/formats/`.

## [1.1.0] - 2026-07

### Added
- Full migration to modern **Avalonia UI** for cross-platform desktop support (Windows and Linux).
- Enhanced `.xrc` resource tree decoding and location hierarchy grafting.
- Mod folder overlay system (`ModFolderWatcher`) with on-the-fly candidate swapping.

## [1.0.6] - 2026-07

### Added
- GLB and OBJ format choices for batch 3D model exports.

## [1.0.5] - 2026-07

### Added
- Image preview automatic refit on window resize.

## [1.0.4] - 2026-07

### Added
- In-app Settings panel (Options → Settings).
- Child process tracker to eliminate zombie ffmpeg processes on shutdown.

## [1.0.0] - 2026-07

### Added
- Initial release of TLJ Explorer:
  - Recursive `.xarc` archive extraction and unified virtual file system.
  - Image decoders for `.xmg` and `.tm` textures.
  - Audio playback for `.isn`, `.iss`, and `.ovs` streams via LibVLC.
  - Video preview for Smacker (`.sss`) and Bink (`.bbb`) with chroma-key transparency support via ffmpeg.
  - 3D skeletal mesh (`.cir`) and animation (`.ani`) previewing via Silk.NET OpenGL.
  - Best-effort scene composite viewer.
