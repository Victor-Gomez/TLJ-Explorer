# BIFF prop mesh

A static prop mesh — furniture, doors, incidental scenery — stored inside a BIFF container. Same generic block wrapper as [TM textures](tm.md); different block `TypeId` values.

Prop meshes are conceptually static — they don't skin to a skeleton — but the format was borrowed from a keyframe-animated mesh format, so vertices still carry per-vertex "anim" targets and the whole mesh carries a transform-keyframe track. This tool applies keyframe 0 as a placement transform and ignores the rest.

## Purpose

BIFF prop meshes carry the geometry, materials, and rest-pose transform for one placed prop. They're referenced from XRC `Item` records.

## Container header

Identical to [`.tm`](tm.md):

```text
Header:
    char[4] Id             -- ASCII "BIFF"
    UInt32  Version        -- 1 or 2
    UInt32  Unknown1
    UInt32  Unknown2
    UInt32  NumBlocks

Block:
    UInt32 BeginMarker
    UInt32 TypeId
    UInt32 Unknown1
    Int32  DataSize
    UInt32 ObjectVersion   -- only present when file Version == 2 (called "Unknown2" in the .tm doc)

    <DataSize bytes of payload, interpreted per TypeId>

    UInt32 EndMarker
    UInt32 NumSubBlocks

    <NumSubBlocks nested Blocks, recursively>
```

The reader always seeks to `payloadStart + DataSize` before reading the trailer, so partial payload consumption still leaves the container self-framing.

## Block types

| TypeId | Meaning |
| --- | --- |
| `0x05a4aa94` | `MeshObjectSceneData` |
| `0x05a4aa89` | `MeshObjectBase` |
| `0x05a4aa8d` | `MeshObjectTri` |
| `0x05a4aa8e` | `MeshObjectMaterial` |

All other TypeIds are opaque and skipped.

### `MeshObjectSceneData` (`0x05a4aa94`)

```text
UInt32 AnimStart
UInt32 AnimEnd
```

Scene-level animation time range. Two `uint32`s. Informational — the mesh itself carries its own keyframe track and does not require these.

### `MeshObjectBase` (`0x05a4aa89`)

```text
String16 Name
```

`String16` here means "`UInt16` length prefix (character count, no NUL) followed by that many ASCII bytes". This is the mesh's display name and nothing else; the rest of the "base" payload — if any — is ignored by both the reader and this documentation. The reader doesn't consume the `HasPhysics` byte here; that byte actually lives at the end of `MeshObjectTri`.

### `MeshObjectTri` (`0x05a4aa8d`)

The geometry chunk. This is where nearly all the mesh data lives. Fields in on-disk order:

```text
String16 Name                        -- mesh sub-name (typically the same as MeshObjectBase's Name)

UInt32 KeyFrameCount
repeat KeyFrameCount times:
    UInt32 Time                       -- integer time value
    Single EssentialRotation.X
    Single EssentialRotation.Y
    Single EssentialRotation.Z
    Single EssentialRotation.W        -- unit quaternion
    Single Determinant
    Single StretchRotation.X
    Single StretchRotation.Y
    Single StretchRotation.Z
    Single StretchRotation.W          -- unit quaternion
    Single Scale.X
    Single Scale.Y
    Single Scale.Z
    Single Translation.X
    Single Translation.Y
    Single Translation.Z

-- The next two fields are ONLY present when the block's ObjectVersion >= 2
-- (which itself is only ever populated when the file Version == 2).
UInt32 UvKeyFrameCount               -- must be 0 in the wild; nonzero triggers "not implemented"
UInt32 AttributeCount                 -- must be 0 in the wild; nonzero triggers "not implemented"

UInt32 VertexCount
repeat VertexCount times:
    String16 AnimName1                -- animation "target" name #1
    String16 AnimName2                -- animation "target" name #2
    Single   AnimInfluence1
    Single   AnimInfluence2
    Single   PosX, PosY, PosZ

UInt32 NormalCount
repeat NormalCount times:
    Single NX, NY, NZ

UInt32 TextureVertexCount
repeat TextureVertexCount times:
    Single U, V, Z                    -- Z is always read but always discarded

UInt32 FaceCount
repeat FaceCount times:
    UInt32 V0, V1, V2                 -- indices into vertex table
    UInt32 N0, N1, N2                 -- indices into normal table
    UInt32 T0, T1, T2                 -- indices into texture-coordinate table
    UInt32 MaterialId
    UInt32 SmoothingGroup

Byte   HasPhysics                     -- 0 or 1; informational
```

Reader post-pass: the multi-index `(V, N, T)` scheme is collapsed into a single flat vertex array, and faces are batched by `MaterialId` — see [`BiffMeshReader.Reindex`](../../src/TLJExplorer.Core/Formats/BiffMeshReader.cs).

### `MeshObjectMaterial` (`0x05a4aa8e`)

```text
String16 Name
String16 Texture
String16 Alpha
String16 Environment
UInt32   Shading
Single   Ambient.R, Ambient.G, Ambient.B
Single   Diffuse.R, Diffuse.G, Diffuse.B
Single   Specular.R, Specular.G, Specular.B
Single   Shininess
Single   Opacity
Byte     DoubleSided                  -- 0 or 1
UInt32   TextureTiling
UInt32   AlphaTiling
UInt32   EnvironmentTiling
Byte     IsColorKey                   -- 0 or 1
UInt32   ColorKey                     -- packed 32-bit colour, meaningful only when IsColorKey == 1

UInt32 AttributeCount                 -- must be 0; nonzero triggers "not implemented"
```

Note: `DoubleSided` and `IsColorKey` are single bytes here, not `bool32`s as in XRC records.

## Semantics

- **Exactly one `MeshObjectTri` per archive.** The reader throws if the archive contains zero or two of them. `MeshObjectMaterial` and `MeshObjectSceneData` chunks may appear any number of times.
- **Triple-indexed faces are collapsed at load time.** The raw layout indexes positions, normals, and texture-coordinates separately (Wavefront-OBJ style). The reader emits one unique vertex per `(V, N, T)` combination and groups faces by `MaterialId`.
- **`MaterialId` is not an array index.** It is a stable material handle carried on face groups. Materials may not appear in the order groups reference them.
- **`Determinant` is stored explicitly alongside the two rotations.** The pair `(EssentialRotation, Determinant, StretchRotation, Scale, Translation)` is a polar decomposition of the mesh's world transform — the essential quaternion is the "true" rotation, and stretch × scale plus the sign captured by determinant reconstructs any mirroring.
- **Only keyframe 0 is used.** The rest of the keyframe track describes an animation of the entire mesh transform (translation + full rotation + scale) over time; this tool applies keyframe 0's transform as a placement and drops the rest.
- **`UvKeyFrameCount`/`AttributeCount` are hard-error canaries.** Both fields exist in the on-disk schema but their handlers are unimplemented. In practice every file the game ships has zero for both; anything else throws so the reader doesn't silently produce wrong geometry.
- **Texture-coord `Z` is always discarded.** UV data is stored as a `Vec3` on disk; only the first two floats are used. This matches ScummVM's engine.

## Known unknowns

- Every `Unknown*` field in the outer BIFF wrapper — same as [`.tm`](tm.md).
- Per-vertex `AnimName1`, `AnimName2`, `AnimInfluence1`, `AnimInfluence2` — read but not used by the renderer. These are placeholders from the format's animated-mesh origin.
- Per-face `SmoothingGroup` — read but not used.
- Material `Shading`, `Ambient`, `Specular`, `Shininess`, `TextureTiling`, `AlphaTiling`, `EnvironmentTiling` — read into the material record but not applied by the renderer.

## Decoder

- Typed reader: [`BiffMeshReader`](../../src/TLJExplorer.Core/Formats/BiffMeshReader.cs)
- Types: [`BiffMesh`](../../src/TLJExplorer.Core/Formats/BiffMesh.cs)
- Text dumper: [`BiffDump`](../../src/TLJExplorer.Core/Formats/BiffDump.cs) — same walk, human-readable output.
- CIR-shaped adapter: [`BiffToCirAdapter`](../../src/TLJExplorer.Core/Formats/BiffToCirAdapter.cs) — lets prop meshes reuse the character-model renderer.
