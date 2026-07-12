namespace TLJExplorer.Core.Formats;

/// <summary>
/// A material from a BIFF prop-mesh archive. Layout matches ScummVM's
/// <c>engines/stark/formats/biffmesh.cpp</c> <c>MeshObjectMaterial</c>.
/// </summary>
public sealed record BiffMaterial(
    string Name,
    string Texture,
    string Alpha,
    string Environment,
    uint Shading,
    (float R, float G, float B) Ambient,
    (float R, float G, float B) Diffuse,
    (float R, float G, float B) Specular,
    float Shininess,
    float Opacity,
    bool DoubleSided,
    uint TextureTiling,
    uint AlphaTiling,
    uint EnvironmentTiling,
    bool IsColorKey,
    uint ColorKey);

/// <summary>
/// A single skinned-ish vertex in a BIFF prop mesh. BIFF meshes are conceptually static, but each vertex
/// carries two "animName" targets and per-target weights (matching ScummVM's <c>MeshObjectTri::Vertex</c>).
/// Consumers that only need the geometry can ignore the anim fields and read <see cref="Position"/>
/// directly.
/// </summary>
public sealed record BiffVertex(
    string AnimName1,
    string AnimName2,
    float AnimInfluence1,
    float AnimInfluence2,
    (float X, float Y, float Z) Position);

/// <summary>
/// A face triangle in the raw BIFF stream: three multi-index tuples plus a material and smoothing group.
/// Consumers that just want indexed geometry should use <see cref="BiffMeshGroup.VertexIndices"/> after
/// reindexing via <see cref="BiffMeshReader"/>.
/// </summary>
public sealed record BiffFace(
    int V0, int V1, int V2,
    int N0, int N1, int N2,
    int T0, int T1, int T2,
    uint MaterialId,
    uint SmoothingGroup);

/// <summary>
/// A batched face group after reindexing: all triangles that share <see cref="MaterialId"/>, expressed as a
/// flat <see cref="VertexIndices"/> array (three consecutive entries per triangle) into the reindexed
/// <see cref="BiffMesh.Vertices"/> array.
/// </summary>
public sealed record BiffMeshGroup(uint MaterialId, int[] VertexIndices);

/// <summary>
/// A reindexed vertex ready for GPU consumption: position + normal + texture coordinate.
/// Produced from the raw BIFF (position, normal, texture-position) multi-index scheme by
/// <see cref="BiffMeshReader"/>'s reindex pass.
/// </summary>
public sealed record BiffReindexedVertex(
    (float X, float Y, float Z) Position,
    (float X, float Y, float Z) Normal,
    (float U, float V) TexCoord);

/// <summary>
/// A keyframe of the whole mesh transform (translation + rotation + stretch + scale + determinant), as
/// stored in the BIFF file. The transform for keyframe 0 is typically applied once to place the mesh in
/// world space; animation of these keyframes is not supported by this tool.
/// </summary>
public sealed record BiffKeyFrame(
    uint Time,
    (float X, float Y, float Z, float W) EssentialRotation,
    float Determinant,
    (float X, float Y, float Z, float W) StretchRotation,
    (float X, float Y, float Z) Scale,
    (float X, float Y, float Z) Translation);

/// <summary>
/// A fully decoded BIFF prop-mesh archive: a single mesh + its materials.
/// </summary>
public sealed class BiffMesh
{
    /// <summary>Optional scene-level animation range (from a <c>MeshObjectSceneData</c> chunk).</summary>
    public uint? AnimStart { get; init; }

    /// <summary>Optional scene-level animation range (from a <c>MeshObjectSceneData</c> chunk).</summary>
    public uint? AnimEnd { get; init; }

    /// <summary>Mesh name from <see cref="BiffMeshReader"/> (the <c>MeshObjectTri</c> string16 name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Every keyframe as read from the file; typically only <c>[0]</c> is applied.</summary>
    public BiffKeyFrame[] KeyFrames { get; init; } = [];

    /// <summary>Materials by index; face groups reference these via <see cref="BiffMeshGroup.MaterialId"/>.</summary>
    public BiffMaterial[] Materials { get; init; } = [];

    /// <summary>Reindexed vertex array; face groups' indices point in here.</summary>
    public BiffReindexedVertex[] Vertices { get; init; } = [];

    /// <summary>One entry per material actually used by at least one face.</summary>
    public BiffMeshGroup[] Groups { get; init; } = [];

    /// <summary>ScummVM's <c>hasPhysics</c> byte, purely informational for tooling.</summary>
    public bool HasPhysics { get; init; }
}
