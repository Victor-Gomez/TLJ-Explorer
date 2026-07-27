# ANI — skeletal animation

Per-bone tracks of `(rotation, translation)` keyframes sampled over time. Paired with a `.cir` skeleton: the bone indices in an ANI file are indices into the CIR's `Skeleton` array.

## Purpose

ANI is played by driving each bone in a CIR skeleton with a sequence of keyframes: rotate the bone by the keyframe's quaternion, translate by its position vector, at the given `AnimTime`. The renderer interpolates between adjacent keys at runtime.

## Layout

```text
Header:
    Int32 Id             -- must equal 3
    Int32 Version        -- 3 or 256
```

The rest of the preamble differs between the two versions — same fields, **different order**:

```text
if Version == 3:
    Unknown1  = 0            -- NOT read from the stream; simply defaulted
    Int32  MaxTime
    UInt32 Magic             -- must equal 0xDEADBABE

if Version == 256:
    Int32  Unknown1
    UInt32 Magic             -- must equal 0xDEADBABE
    Int32  MaxTime           -- read LAST for this version
```

Then the body:

```text
BoneAnims: array of {
    Int32 BoneIndex          -- index into the paired CIR's Skeleton array
    KeyDatas: array of {
        Int32  AnimTime      -- ordering key; monotonically increasing within a track
        Single QRotX
        Single QRotY
        Single QRotZ
        Single QRotW         -- quaternion (x, y, z, w) applied to the bone
        Single PosX
        Single PosY
        Single PosZ          -- translation applied to the bone
    }
}
```

As with CIR, all arrays are `Int32`-length-prefixed.

## Semantics

- **Bone-index-driven, not dense.** A single ANI file only carries tracks for the bones it actually animates — a bone not present in the file stays in its rest pose. This is why bone indices are stored explicitly on each track (`BoneIndex`) rather than being implicit.
- **`AnimTime` is a timeline sample, not a real timestamp.** It orders keys within a track and gates their contribution when interpolating. `MaxTime` is the last valid time value across the whole animation.
- **Interpolation happens at playback time.** The file stores discrete keys only. The playback path — [`AnimationSampler`](../../src/TLJExplorer.Core/Formats/AnimationSampler.cs) → [`SkeletonPoser`](../../src/TLJExplorer/Rendering/SkeletonPoser.cs) — handles slerp on the quaternion and lerp on the position between the two bracketing keys.
- **`0xDEADBABE` is the same alignment canary as in CIR.** A wrong value means the version-dependent preamble was mis-parsed.

## Version differences

There is no header size difference between V3 and V256 — both add up to five 32-bit words after `Id`/`Version` — but their **field order is different**, and `Unknown1` is *not read from the stream* in V3 (it's defaulted to 0). This is a genuine quirk of the format, not a bug: it is replicated exactly in the decoder as it was in the original TLJView reverse-engineering.

## Known unknowns

- `Unknown1` (V256 only) and `Unknown2` in the header (`Unknown2` appears in the V3 layout under a different name in the source and is dropped from the reader path).

## Decoder

- [`AniDecoder`](../../src/TLJExplorer.Core/Formats/AniDecoder.cs)
- Animation types: [`AniAnimation`](../../src/TLJExplorer.Core/Formats/AniAnimation.cs)
- Runtime sampling: [`AnimationSampler`](../../src/TLJExplorer.Core/Formats/AnimationSampler.cs)
- Tests: [`AnimationSamplerTests`](../../tests/TLJExplorer.Core.Tests/AnimationSamplerTests.cs)
