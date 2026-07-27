# OVS / SSS / BBB — passthrough containers

Three archive extensions store an unmodified well-known media stream under a TLJ-specific name. There is no wrapping, no header, no transformation — the archive just renames the file.

| Extension | Payload | Extract as |
| --- | --- | --- |
| `.ovs` | Ogg Vorbis audio | `.ogg` |
| `.sss` | Smacker video | `.smk` |
| `.bbb` | Bink video | `.bik` |

"Extraction" is a verbatim byte copy. No header rewriting, no re-encoding.

## Playback

- **`.ogg`** — decoded to PCM WAV via [`OggDecoder`](../../src/TLJExplorer.Core/Formats/OggDecoder.cs) (using [NVorbis](https://github.com/NVorbis/NVorbis), pure managed) so playback works on hosts that don't ship an Ogg codec. WAV plays everywhere.
- **`.smk` / `.bik`** — transcoded to `.mp4` on demand by the bundled `ffmpeg` binary via [`FfmpegTranscoder`](../../src/TLJExplorer/Services/FfmpegTranscoder.cs). The path to ffmpeg is either `ffmpeg/ffmpeg[.exe]` next to the built binary or an override set in `AppSettings.FfmpegPath`. The transcoded file is played back through LibVLC.

## Sanity check

The three passthrough extensions are all the archive uses; there is no third audio wrapper. If a file has one of these extensions but doesn't start with a valid Ogg / Smacker / Bink signature, the archive is corrupt or has been re-encoded — the extract tool doesn't validate this, it just copies.

## Decoder

- [`ContainerUnwrap`](../../src/TLJExplorer.Core/Formats/ContainerUnwrap.cs) — the extension mapping and the verbatim copy.
- Extraction from ffmpeg is handled by [`FfmpegTranscoder`](../../src/TLJExplorer/Services/FfmpegTranscoder.cs).
