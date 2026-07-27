# CIR — 3D skeletal model

Character and prop meshes with a bone skeleton. Each vertex stores **two** positions — one in each of two bone reference frames — and blends between them by a per-vertex weight. This is the game's take on two-bone linear skinning, done in the vertex data itself rather than by a matrix palette at runtime.

## Purpose

CIR holds the geometry, materials, and skeleton for one character or prop. Animation (`.ani`) drives the same skeleton with per-frame quaternion + position keys; the mesh vertices then blend between two bones' reference frames using their stored dual positions.

## Conventions

- All **strings** are length-prefixed: `UInt32 Length` followed by that many raw ASCII bytes. No NUL terminator.
- All **arrays** are length-prefixed: `Int32 Count` followed by that many entries.
- All multi-byte values are little-endian; all `Single`s are IEEE-754 32-bit.

## Layout

```text
Header:
    Int32  Id            -- must equal 4
    Int32  Version       -- must be 16 or 256
    Int32  Unknown1      -- ONLY present when Version == 256
    UInt32 Magic         -- must equal 0xDEADBABE
    Single Unknown3
```

**Materials:**

```text
Materials: array of {
    String  Name
    Int32   Unknown1
    String  TextureName
    Single  ColourR
    Single  ColourG
    Single  ColourB
}
```

**Unknown4** (likely bounding volumes / hulls):

```text
Unknown4: array of {
    Single[4]           -- meaning not derived
}
```

**Skeleton:**

```text
Skeleton: array of {
    String  Name
    Single  Unknown1
    Children: array of Int32     -- indices back into this Skeleton array
}
```

The bone graph is defined implicitly: bone `i` lists the indices of its children. Root bones are the ones no other bone references.

**Groups:**

```text
Groups: array of {
    String  Name

    Faces: array of {
        Int32 MaterialIndex

        Vertices: array of {
            Single PosX1, PosY1, PosZ1        -- position in bone reference frame 1
            Single PosX2, PosY2, PosZ2        -- SAME vertex, position in bone reference frame 2
            Single NormalX, NormalY, NormalZ
            Single TextureS, TextureT
            Int32  BoneIndex1
            Int32  BoneIndex2
            Single BoneWeight                 -- weight for frame 1; frame 2 uses (1 - BoneWeight)
        }

        Triangles: array of {
            Int32 VertexIndex1
            Int32 VertexIndex2
            Int32 VertexIndex3
        }
    }

    Unknown1: array of { Single[4], Int32 }
    Unknown2: array of { String Unknown2_01, Single[8], Int32 }
}
```

## Semantics

- **Skinning is dual-position, not matrix-palette.** Each vertex carries two full positions (`Pos1` and `Pos2`), each expressed in the local reference frame of a specific bone (`BoneIndex1` and `BoneIndex2`). At render time you transform `Pos1` by the world-space transform of `BoneIndex1` and `Pos2` by the world-space transform of `BoneIndex2`, then blend the two world-space positions by `BoneWeight` (for `Pos1`) and `1 - BoneWeight` (for `Pos2`). See [`ModelRenderer`](../../src/TLJExplorer/Rendering/ModelRenderer.cs) and [`SkeletonPoser`](../../src/TLJExplorer/Rendering/SkeletonPoser.cs).
- **Bone reference-frame globals.** The runtime needs a global (rest-pose) transform for each bone to compose an `.ani`-driven pose. [`CirBoneGlobals`](../../src/TLJExplorer.Core/Formats/CirBoneGlobals.cs) computes these by walking the skeleton hierarchy.
- **Only `Groups[0]` is rendered by the game.** Subsequent groups are preserved by the decoder for fidelity but the renderer only uses the first one. If your file has multiple, treat the rest as inert.
- **Faces bundle geometry with material.** A single "Face" holds a full set of vertices and triangles that use one material. A model with N materials produces N faces per group.
- **`0xDEADBABE` guards frame alignment.** The magic word between `Version`-dependent preamble and the body is the format's alignment canary. A wrong value means everything downstream is garbage — the decoder throws.

## Version differences

Only the header differs:

- **Version 16:** header is `Id, Version, Magic, Unknown3` (no `Unknown1`).
- **Version 256:** an extra `Int32 Unknown1` is present between `Version` and `Magic`.

Everything from `Materials` onwards is identical.

## Export

- **glTF (`.glb`)** — [`GlbWriter`](../../src/TLJExplorer.Core/Formats/GlbWriter.cs) writes a binary glTF with the mesh, materials (referencing extracted textures), and skeleton. This is the recommended export target — vertices, bones, and animations round-trip.
- **Wavefront OBJ (`.obj`)** — [`ObjWriter`](../../src/TLJExplorer.Core/Formats/ObjWriter.cs) writes a static mesh in the model's rest pose. No bones, no animation — suitable for prop meshes or reference art.

## Known unknowns

- `Unknown1`/`Unknown2` before `Magic` (see version notes).
- `Unknown3` (`Single` after `Magic`).
- Every material's `Unknown1` `Int32`.
- Every skeleton bone's `Unknown1` `Single` (possibly a length/radius hint but the renderer doesn't use it).
- The `Unknown4` block (array of 4-float tuples between materials and skeleton).
- Per-group `Unknown1` (array of `Single[4], Int32`) and `Unknown2` (array of `String, Single[8], Int32`).

## Decoder

- [`CirDecoder`](../../src/TLJExplorer.Core/Formats/CirDecoder.cs)
- Model types: [`CirModel`](../../src/TLJExplorer.Core/Formats/CirModel.cs)
- Bone rest-pose globals: [`CirBoneGlobals`](../../src/TLJExplorer.Core/Formats/CirBoneGlobals.cs)
- Tests: [`CirBoneGlobalsTests`](../../tests/TLJExplorer.Core.Tests/CirBoneGlobalsTests.cs)
