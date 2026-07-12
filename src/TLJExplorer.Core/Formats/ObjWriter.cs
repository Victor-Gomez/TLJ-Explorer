using System.Globalization;
using System.Numerics;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Writes a <see cref="CirModel"/> as a Wavefront OBJ + MTL pair. Emits an <em>unposed</em> mesh: every
/// vertex's two bone-local positions are blended by <see cref="CirVertex.BoneWeight"/> against an
/// identity-bone skeleton, producing the same "rest" data the in-app viewer shows when no animation is
/// loaded. For BIFF meshes routed through <see cref="BiffToCirAdapter"/> this happens to be the correct
/// world-space geometry; for real CIR characters it is a rest pose (not necessarily the bind pose).
/// </summary>
public static class ObjWriter
{
    /// <summary>
    /// Writes <paramref name="model"/> to <paramref name="objPath"/> and a sibling <c>.mtl</c> file. Pass
    /// <paramref name="bindPoseAnimation"/> to bake the ANI's first keyframe into vertex positions/normals
    /// — required for real CIR characters, harmless for BIFF props (which have the transform baked in).
    /// The MTL is only emitted when the model has at least one material; the OBJ references it via
    /// <c>mtllib</c>.
    /// </summary>
    public static void Write(CirModel model, string objPath, AniAnimation? bindPoseAnimation = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(objPath);

        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        string mtlName = Path.GetFileName(mtlPath);

        CirBoneBindPose[]? bones = bindPoseAnimation is not null && model.Skeleton.Length > 0
            ? CirBoneGlobals.Compute(model, bindPoseAnimation)
            : null;

        using (var obj = new StreamWriter(objPath, append: false, Encoding.ASCII))
        {
            WriteObj(obj, model, mtlName, bones);
        }

        if (model.Materials.Length > 0)
        {
            using var mtl = new StreamWriter(mtlPath, append: false, Encoding.ASCII);
            WriteMtl(mtl, model);
        }
    }

    private static void WriteObj(StreamWriter w, CirModel model, string mtlName, CirBoneBindPose[]? bones)
    {
        w.WriteLine("# Exported by TLJ Explorer");
        if (model.Materials.Length > 0)
        {
            w.Write("mtllib ");
            w.WriteLine(mtlName);
        }

        if (model.Groups.Length == 0)
            return;

        CirGroup group = model.Groups[0];
        if (!string.IsNullOrEmpty(group.Name))
        {
            w.Write("o ");
            w.WriteLine(group.Name);
        }

        // OBJ indices are 1-based and global across the whole file. Emit each face batch's vertices
        // in-line and track its base index so faces can reference the right block.
        int vertexBase = 1;

        foreach (CirFace face in group.Faces)
        {
            if (face.Vertices.Length == 0 || face.Triangles.Length == 0)
                continue;

            foreach (CirVertex v in face.Vertices)
            {
                (Vector3 pos, Vector3 normal) = ResolveVertexPose(v, bones);
                w.Write("v ");
                WriteVec3(w, pos);
                w.WriteLine();

                w.Write("vn ");
                WriteVec3(w, normal);
                w.WriteLine();

                w.Write("vt ");
                w.Write(v.TextureS.ToString("G7", CultureInfo.InvariantCulture));
                w.Write(' ');
                // OBJ's V axis points up while our textures follow the image origin (top-left); flip so
                // the exported mesh matches how it's displayed in-viewer.
                w.WriteLine((1f - v.TextureT).ToString("G7", CultureInfo.InvariantCulture));
            }

            if (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Length)
            {
                w.Write("usemtl ");
                w.WriteLine(MakeMaterialName(model.Materials[face.MaterialIndex], face.MaterialIndex));
            }

            foreach (CirTriangle t in face.Triangles)
            {
                int a = vertexBase + t.VertexIndex1;
                int b = vertexBase + t.VertexIndex2;
                int c = vertexBase + t.VertexIndex3;

                w.Write("f ");
                WriteFaceCorner(w, a);
                w.Write(' ');
                WriteFaceCorner(w, b);
                w.Write(' ');
                WriteFaceCorner(w, c);
                w.WriteLine();
            }

            vertexBase += face.Vertices.Length;
        }
    }

    private static void WriteMtl(StreamWriter w, CirModel model)
    {
        w.WriteLine("# Exported by TLJ Explorer");
        for (int i = 0; i < model.Materials.Length; i++)
        {
            CirMaterial m = model.Materials[i];
            string name = MakeMaterialName(m, i);

            w.Write("newmtl ");
            w.WriteLine(name);
            w.WriteLine("Ka 0.35 0.35 0.35");
            w.Write("Kd ");
            w.Write(m.ColourR.ToString("G7", CultureInfo.InvariantCulture));
            w.Write(' ');
            w.Write(m.ColourG.ToString("G7", CultureInfo.InvariantCulture));
            w.Write(' ');
            w.WriteLine(m.ColourB.ToString("G7", CultureInfo.InvariantCulture));
            w.WriteLine("Ks 0.25 0.25 0.25");
            w.WriteLine("Ns 32");
            if (!string.IsNullOrEmpty(m.TextureName))
            {
                w.Write("map_Kd ");
                w.WriteLine(m.TextureName);
            }
        }
    }

    private static void WriteFaceCorner(StreamWriter w, int index)
    {
        string s = index.ToString(CultureInfo.InvariantCulture);
        w.Write(s);
        w.Write('/');
        w.Write(s);
        w.Write('/');
        w.Write(s);
    }

    private static void WriteVec3(StreamWriter w, Vector3 v)
    {
        w.Write(v.X.ToString("G7", CultureInfo.InvariantCulture));
        w.Write(' ');
        w.Write(v.Y.ToString("G7", CultureInfo.InvariantCulture));
        w.Write(' ');
        w.Write(v.Z.ToString("G7", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Blends a vertex's two bone-local positions into world space. When <paramref name="bones"/> is
    /// non-null, uses the sampled bind pose (which is the only correct choice for real CIR characters);
    /// otherwise falls back to identity-bone blending (which is fine for BIFF-adapted meshes since the
    /// transform is already baked into vertices).
    /// </summary>
    private static (Vector3 Pos, Vector3 Normal) ResolveVertexPose(CirVertex v, CirBoneBindPose[]? bones)
    {
        var pos1 = new Vector3(v.PosX1, v.PosY1, v.PosZ1);
        var pos2 = new Vector3(v.PosX2, v.PosY2, v.PosZ2);
        var normal = new Vector3(v.NormalX, v.NormalY, v.NormalZ);
        float w = Math.Clamp(v.BoneWeight, 0f, 1f);

        Vector3 wp1, wp2, wn1, wn2;
        if (bones is not null && InRange(v.BoneIndex1, bones.Length) && InRange(v.BoneIndex2, bones.Length))
        {
            wp1 = CirBoneGlobals.ToWorld(bones[v.BoneIndex1], pos1);
            wp2 = CirBoneGlobals.ToWorld(bones[v.BoneIndex2], pos2);
            wn1 = CirBoneGlobals.RotateDirection(bones[v.BoneIndex1], normal);
            wn2 = CirBoneGlobals.RotateDirection(bones[v.BoneIndex2], normal);
        }
        else
        {
            wp1 = pos1; wp2 = pos2;
            wn1 = normal; wn2 = normal;
        }

        Vector3 pos = (wp1 * w) + (wp2 * (1f - w));
        Vector3 finalNormal = (wn1 * w) + (wn2 * (1f - w));
        if (finalNormal.LengthSquared() > 1e-12f)
            finalNormal = Vector3.Normalize(finalNormal);

        return (pos, finalNormal);
    }

    private static bool InRange(int i, int n) => i >= 0 && i < n;

    private static string MakeMaterialName(CirMaterial m, int index)
    {
        string raw = string.IsNullOrEmpty(m.Name)
            ? (string.IsNullOrEmpty(m.TextureName) ? $"material_{index}" : m.TextureName)
            : m.Name;

        Span<char> buffer = stackalloc char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            buffer[i] = char.IsWhiteSpace(c) ? '_' : c;
        }

        return new string(buffer);
    }
}
