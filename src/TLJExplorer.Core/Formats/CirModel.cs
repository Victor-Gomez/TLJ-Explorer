namespace TLJExplorer.Core.Formats;

/// <summary>A material referenced by faces in a <see cref="CirModel"/>.</summary>
/// <param name="Name">Material name.</param>
/// <param name="Unknown1">Reverse-engineered field, purpose unknown.</param>
/// <param name="TextureName">Name of the texture (looked up elsewhere, e.g. in a TM file) used by this material.</param>
/// <param name="ColourR">Red component of the material's base colour.</param>
/// <param name="ColourG">Green component of the material's base colour.</param>
/// <param name="ColourB">Blue component of the material's base colour.</param>
public sealed record CirMaterial(string Name, int Unknown1, string TextureName, float ColourR, float ColourG, float ColourB);

/// <summary>
/// A record of unknown purpose stored alongside the skeleton/materials; likely bounding-box or
/// bounding-sphere data (4 floats per entry). Not yet confirmed.
/// </summary>
/// <param name="Values">The 4 raw float values as stored in the file.</param>
public sealed record CirUnknown4Entry(float[] Values);

/// <summary>
/// A bone in the model's skeleton. Bones form a tree via <see cref="Children"/>, which holds indices
/// back into the owning <see cref="CirModel.Skeleton"/> array. The root bone is conceptually index 0.
/// </summary>
/// <param name="Name">Bone name.</param>
/// <param name="Unknown1">Reverse-engineered field, purpose unknown.</param>
/// <param name="Children">Indices of this bone's child bones within <see cref="CirModel.Skeleton"/>.</param>
public sealed record CirBone(string Name, float Unknown1, int[] Children);

/// <summary>
/// A single skinned vertex.
/// </summary>
/// <remarks>
/// This format uses a dual-position two-bone-blend skinning scheme rather than classic single-position
/// skinning: the SAME logical vertex stores two separate positions, (<see cref="PosX1"/>, <see cref="PosY1"/>,
/// <see cref="PosZ1"/>) and (<see cref="PosX2"/>, <see cref="PosY2"/>, <see cref="PosZ2"/>), each expressed in the
/// local reference frame of a different bone (<see cref="BoneIndex1"/> and <see cref="BoneIndex2"/>
/// respectively). The final vertex position is obtained by transforming each position by its own bone's
/// current transform and blending the two results using <see cref="BoneWeight"/> for frame 1 and
/// <c>(1 - BoneWeight)</c> for frame 2.
/// </remarks>
/// <param name="PosX1">Position X in bone reference frame 1.</param>
/// <param name="PosY1">Position Y in bone reference frame 1.</param>
/// <param name="PosZ1">Position Z in bone reference frame 1.</param>
/// <param name="PosX2">Position X in bone reference frame 2 (same vertex, different bone-local frame).</param>
/// <param name="PosY2">Position Y in bone reference frame 2.</param>
/// <param name="PosZ2">Position Z in bone reference frame 2.</param>
/// <param name="NormalX">Vertex normal X.</param>
/// <param name="NormalY">Vertex normal Y.</param>
/// <param name="NormalZ">Vertex normal Z.</param>
/// <param name="TextureS">Texture coordinate S (U).</param>
/// <param name="TextureT">Texture coordinate T (V).</param>
/// <param name="BoneIndex1">Index (into the model's skeleton) of the bone that owns reference frame 1.</param>
/// <param name="BoneIndex2">Index (into the model's skeleton) of the bone that owns reference frame 2.</param>
/// <param name="BoneWeight">Blend weight applied to frame 1's transformed position; frame 2 uses <c>1 - BoneWeight</c>.</param>
public sealed record CirVertex(
    float PosX1, float PosY1, float PosZ1,
    float PosX2, float PosY2, float PosZ2,
    float NormalX, float NormalY, float NormalZ,
    float TextureS, float TextureT,
    int BoneIndex1, int BoneIndex2, float BoneWeight);

/// <summary>A triangle referencing three vertices by index within the owning <see cref="CirFace"/>.</summary>
public sealed record CirTriangle(int VertexIndex1, int VertexIndex2, int VertexIndex3);

/// <summary>A face batch: all triangles/vertices sharing a single material.</summary>
/// <param name="MaterialIndex">Index into <see cref="CirModel.Materials"/>.</param>
/// <param name="Vertices">Vertices used by this face's triangles.</param>
/// <param name="Triangles">Triangles, each indexing into <paramref name="Vertices"/>.</param>
public sealed record CirFace(int MaterialIndex, CirVertex[] Vertices, CirTriangle[] Triangles);

/// <summary>
/// A face-group-level record of unknown purpose (4 floats followed by an int), stored per <see cref="CirGroup"/>.
/// </summary>
public sealed record CirGroupUnknown1Entry(float Value1, float Value2, float Value3, float Value4, int Value5);

/// <summary>
/// A face-group-level record of unknown purpose (a string followed by 8 floats and an int), stored per
/// <see cref="CirGroup"/>.
/// </summary>
public sealed record CirGroupUnknown2Entry(
    string Unknown2_01,
    float Value1, float Value2, float Value3, float Value4,
    float Value5, float Value6, float Value7, float Value8,
    int Value9);

/// <summary>
/// A named mesh group made up of one or more material-batched <see cref="CirFace"/> entries.
/// </summary>
/// <remarks>
/// A CIR model may declare several groups (e.g. alternate LODs or variants), but only
/// <c>Groups[0]</c> is the one actually used/rendered by the game.
/// </remarks>
/// <param name="Name">Group name.</param>
/// <param name="Faces">The face batches making up this group's geometry.</param>
/// <param name="Unknown1">Reverse-engineered per-group table, purpose unknown.</param>
/// <param name="Unknown2">Reverse-engineered per-group table, purpose unknown.</param>
public sealed record CirGroup(
    string Name,
    CirFace[] Faces,
    CirGroupUnknown1Entry[] Unknown1,
    CirGroupUnknown2Entry[] Unknown2);

/// <summary>
/// A fully decoded CIR 3D skeletal model: materials, skeleton (bone tree), and one or more mesh groups.
/// Produced by <see cref="CirDecoder"/> from the reverse-engineered binary CIR file format.
/// </summary>
public sealed class CirModel
{
    /// <summary>File format version, either 16 or 256.</summary>
    public int Version { get; init; }

    /// <summary>Materials referenced by face batches via <see cref="CirFace.MaterialIndex"/>.</summary>
    public CirMaterial[] Materials { get; init; } = [];

    /// <summary>Reverse-engineered table of unknown purpose, likely bounding info.</summary>
    public CirUnknown4Entry[] Unknown4 { get; init; } = [];

    /// <summary>The model's bone tree. The root bone is conceptually index 0.</summary>
    public CirBone[] Skeleton { get; init; } = [];

    /// <summary>Mesh groups. <c>Groups[0]</c> is the one actually used/rendered.</summary>
    public CirGroup[] Groups { get; init; } = [];
}
