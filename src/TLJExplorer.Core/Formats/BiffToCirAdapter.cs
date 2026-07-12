using System.Numerics;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Bridges the static <see cref="BiffMesh"/> world into the animated <see cref="CirModel"/> world so the
/// existing model renderer/exporter can display and export BIFF prop meshes with no additional
/// pipeline. The generated <see cref="CirModel"/> has:
/// <list type="bullet">
///   <item><description>One dummy identity bone that every vertex references twice (weight = 1.0).</description></item>
///   <item><description>Keyframe-0's transform baked into every vertex position/normal, matching how
///     ScummVM's renderer applies <c>getTransform(0)</c>.</description></item>
///   <item><description>One <see cref="CirFace"/> per material batch, with per-batch reindexed vertices.</description></item>
/// </list>
/// </summary>
public static class BiffToCirAdapter
{
    /// <summary>
    /// Converts <paramref name="mesh"/> into a <see cref="CirModel"/> suitable for the renderer / glTF
    /// exporter. Never returns null; a mesh with no groups still produces a valid empty model.
    /// </summary>
    public static CirModel ToCirModel(BiffMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        Matrix4x4 transform = mesh.KeyFrames.Length > 0
            ? BuildKeyFrameTransform(mesh.KeyFrames[0])
            : Matrix4x4.Identity;

        var materials = new CirMaterial[mesh.Materials.Length];
        for (int i = 0; i < mesh.Materials.Length; i++)
        {
            BiffMaterial m = mesh.Materials[i];
            materials[i] = new CirMaterial(
                Name: m.Name,
                Unknown1: 0,
                TextureName: m.Texture,
                ColourR: m.Diffuse.R,
                ColourG: m.Diffuse.G,
                ColourB: m.Diffuse.B);
        }

        var faces = new List<CirFace>(mesh.Groups.Length);
        foreach (BiffMeshGroup group in mesh.Groups)
        {
            if (group.VertexIndices.Length == 0)
                continue;

            (CirVertex[] batchVerts, CirTriangle[] batchTris) = BuildBatch(mesh.Vertices, group.VertexIndices, transform);
            int materialIndex = (int)Math.Min(group.MaterialId, (uint)Math.Max(0, materials.Length - 1));
            faces.Add(new CirFace(materialIndex, batchVerts, batchTris));
        }

        var bones = new CirBone[]
        {
            new(Name: "root", Unknown1: 0f, Children: Array.Empty<int>()),
        };

        var groupsOut = new CirGroup[]
        {
            new(
                Name: string.IsNullOrEmpty(mesh.Name) ? "biff" : mesh.Name,
                Faces: faces.ToArray(),
                Unknown1: Array.Empty<CirGroupUnknown1Entry>(),
                Unknown2: Array.Empty<CirGroupUnknown2Entry>()),
        };

        return new CirModel
        {
            Version = 16,
            Materials = materials,
            Unknown4 = Array.Empty<CirUnknown4Entry>(),
            Skeleton = bones,
            Groups = groupsOut,
        };
    }

    private static (CirVertex[] Verts, CirTriangle[] Tris) BuildBatch(
        BiffReindexedVertex[] sourceVertices,
        int[] vertexIndices,
        Matrix4x4 transform)
    {
        // Each entry in vertexIndices is a per-face index; three consecutive entries form a triangle.
        // The CIR renderer expects per-face-batch vertices indexed 0..N-1, so we compact source indices
        // to a per-batch table.
        var remap = new Dictionary<int, int>(vertexIndices.Length);
        var verts = new List<CirVertex>(vertexIndices.Length);

        int triangleCount = vertexIndices.Length / 3;
        var tris = new CirTriangle[triangleCount];

        for (int t = 0; t < triangleCount; t++)
        {
            int i0 = MapIndex(vertexIndices[(t * 3) + 0]);
            int i1 = MapIndex(vertexIndices[(t * 3) + 1]);
            int i2 = MapIndex(vertexIndices[(t * 3) + 2]);
            tris[t] = new CirTriangle(i0, i1, i2);
        }

        return (verts.ToArray(), tris);

        int MapIndex(int sourceIndex)
        {
            if (remap.TryGetValue(sourceIndex, out int existing))
                return existing;

            BiffReindexedVertex src = sourceIndex >= 0 && sourceIndex < sourceVertices.Length
                ? sourceVertices[sourceIndex]
                : new BiffReindexedVertex((0f, 0f, 0f), (0f, 1f, 0f), (0f, 0f));

            var pos = Vector3.Transform(new Vector3(src.Position.X, src.Position.Y, src.Position.Z), transform);
            var normal = Vector3.TransformNormal(new Vector3(src.Normal.X, src.Normal.Y, src.Normal.Z), transform);
            if (normal.LengthSquared() > 1e-12f)
                normal = Vector3.Normalize(normal);

            var cir = new CirVertex(
                PosX1: pos.X, PosY1: pos.Y, PosZ1: pos.Z,
                PosX2: pos.X, PosY2: pos.Y, PosZ2: pos.Z,
                NormalX: normal.X, NormalY: normal.Y, NormalZ: normal.Z,
                TextureS: src.TexCoord.U, TextureT: src.TexCoord.V,
                BoneIndex1: 0, BoneIndex2: 0, BoneWeight: 1f);

            int newIndex = verts.Count;
            verts.Add(cir);
            remap[sourceIndex] = newIndex;
            return newIndex;
        }
    }

    /// <summary>
    /// Reproduces ScummVM's <c>MeshObjectTri::getTransform</c>: T · Rₑ · det · Rₛᵀ · S · Rₛ (each rotation
    /// expanded to a matrix). Applied to the identity-bone vertex positions so the mesh appears in its
    /// authored orientation when rendered without any animation.
    /// </summary>
    private static Matrix4x4 BuildKeyFrameTransform(BiffKeyFrame kf)
    {
        Matrix4x4 translation = Matrix4x4.CreateTranslation(kf.Translation.X, kf.Translation.Y, kf.Translation.Z);
        Matrix4x4 essentialRotation = QuatToMatrix(kf.EssentialRotation);
        Matrix4x4 determinant = Matrix4x4.CreateScale(kf.Determinant, kf.Determinant, kf.Determinant);
        Matrix4x4 stretchRotation = QuatToMatrix(kf.StretchRotation);
        Matrix4x4 stretchRotationT = Matrix4x4.Transpose(stretchRotation);
        Matrix4x4 scale = Matrix4x4.CreateScale(kf.Scale.X, kf.Scale.Y, kf.Scale.Z);

        // System.Numerics matrices are row-major; multiplication order composes right-to-left when
        // transforming a row vector v * M. To match ScummVM's column-vector formula
        //   T * Rₑ * det * Rₛᵀ * S * Rₛ · v
        // we reverse the order so the same overall operation applies to our row-vector convention.
        return stretchRotation * scale * stretchRotationT * determinant * essentialRotation * translation;
    }

    private static Matrix4x4 QuatToMatrix((float X, float Y, float Z, float W) q)
    {
        var quat = new Quaternion(q.X, q.Y, q.Z, q.W);
        return Matrix4x4.CreateFromQuaternion(quat);
    }
}
