using System.Globalization;
using System.Numerics;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>Optional inputs that upgrade <see cref="GlbWriter"/> from a static export to a skinned+animated one.</summary>
public sealed class GlbWriteOptions
{
    /// <summary>
    /// ANI whose first keyframe supplies the bind pose. When provided, vertex positions and normals are
    /// transformed into world space (so the model looks correct in Blender even without any animation
    /// selected), and the emitted file gains a full skin (skeleton nodes, JOINTS_0/WEIGHTS_0 per vertex,
    /// inverse bind matrices). CIR files have no bind pose of their own, so a static export without this
    /// is only correct for BIFF prop meshes (which have the pose baked into vertices already).
    /// </summary>
    public AniAnimation? BindPose { get; init; }

    /// <summary>
    /// Additional animation clips to embed. Each becomes a named glTF animation with rotation+translation
    /// channels per bone. Ignored when <see cref="BindPose"/> is null (glTF animations need a skin).
    /// </summary>
    public IReadOnlyList<(string Name, AniAnimation Animation)> Animations { get; init; } = [];

    /// <summary>
    /// Optional resolver for material textures. Called once per <see cref="CirMaterial.TextureName"/>;
    /// return raw PNG (or JPEG) bytes to embed in the .glb, or <c>null</c> to leave the material flat-coloured.
    /// The Core library doesn't ship a texture decoder-and-PNG-encoder pipeline (that would drag in a UI
    /// framework's imaging stack); wire this up from the app layer where those APIs are available.
    /// </summary>
    public Func<string, byte[]?>? TextureResolver { get; init; }

    public static GlbWriteOptions Default { get; } = new();
}

/// <summary>
/// Writes a <see cref="CirModel"/> as a binary glTF 2.0 file (<c>.glb</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two modes: <b>static</b> (no <see cref="GlbWriteOptions.BindPose"/>) emits one primitive per material
/// batch with identity-blended vertex positions — correct for BIFF prop meshes, garbage for real CIR
/// characters (they have no bind pose of their own, see <c>cir_obj.py</c>).
/// </para>
/// <para>
/// <b>Skinned</b> (with a bind pose ANI) additionally emits: per-bone skeleton nodes with the ANI's
/// first-keyframe as local transforms, <c>JOINTS_0</c>/<c>WEIGHTS_0</c> vertex attributes, inverse bind
/// matrices, and any extra ANIs passed via <see cref="GlbWriteOptions.Animations"/> as glTF animations.
/// Vertex positions and normals are baked to world space at the bind pose so the file opens correctly in
/// Blender/three.js with or without an animation applied.
/// </para>
/// </remarks>
public static class GlbWriter
{
    private const uint MagicGltf = 0x46546C67; // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBin  = 0x004E4942; // "BIN\0"

    private const string ComponentFloat = "5126";
    private const string ComponentUByte = "5121";
    private const string ComponentUShort = "5123";
    private const int TargetArrayBuffer = 34962;
    private const int TargetElementArrayBuffer = 34963;

    public static void Write(CirModel model, string path) => Write(model, path, GlbWriteOptions.Default);

    public static void Write(CirModel model, string path, GlbWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var file = File.Create(path);
        Write(model, file, options);
    }

    public static void Write(CirModel model, Stream output) => Write(model, output, GlbWriteOptions.Default);

    public static void Write(CirModel model, Stream output, GlbWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        var ctx = new Ctx();
        CirBoneBindPose[]? bones = null;
        if (options.BindPose is not null && model.Skeleton.Length > 0)
            bones = CirBoneGlobals.Compute(model, options.BindPose);

        var primitives = new List<PrimitiveInfo>();
        if (model.Groups.Length > 0)
        {
            foreach (CirFace face in model.Groups[0].Faces)
            {
                if (face.Vertices.Length == 0 || face.Triangles.Length == 0)
                    continue;

                primitives.Add(WriteMeshPrimitive(ctx, face, bones));
            }
        }

        int? skinIndex = bones is not null ? 0 : null;
        int? ibmAccessor = bones is not null ? WriteInverseBindMatrices(ctx, bones) : null;

        // Embedded textures: index materials by TextureName so duplicates share one binary blob.
        var materialTextureIndex = new int?[model.Materials.Length];
        var embeddedTextures = new List<EmbeddedTextureInfo>();
        if (options.TextureResolver is not null)
        {
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < model.Materials.Length; i++)
            {
                string texName = model.Materials[i].TextureName;
                if (string.IsNullOrEmpty(texName))
                    continue;
                if (!byName.TryGetValue(texName, out int textureIdx))
                {
                    byte[]? bytes = options.TextureResolver(texName);
                    if (bytes is null || bytes.Length == 0)
                        continue;

                    string mime = LooksLikePng(bytes) ? "image/png" : "image/jpeg";
                    int bufferViewIdx = ctx.AddBufferView(bytes);
                    embeddedTextures.Add(new EmbeddedTextureInfo(bufferViewIdx, mime, texName));
                    textureIdx = embeddedTextures.Count - 1;
                    byName[texName] = textureIdx;
                }
                materialTextureIndex[i] = textureIdx;
            }
        }

        // Animation clips reference glTF nodes 0..N-1 (skeleton nodes always come first when present, so
        // the target-node index equals the bone index). We build the samplers/channels here, then serialise
        // them into the JSON below.
        var animations = new List<AnimationInfo>();
        if (bones is not null)
        {
            foreach ((string name, AniAnimation ani) in options.Animations)
            {
                AnimationInfo? built = TryBuildAnimation(ctx, name, ani, model.Skeleton.Length);
                if (built is not null)
                    animations.Add(built);
            }
        }

        // Everything after this point is JSON assembly + glb chunk framing.
        while ((ctx.Binary.Length & 3) != 0)
            ctx.Binary.WriteByte(0);
        byte[] binaryBytes = ctx.Binary.ToArray();

        string json = BuildJson(model, ctx, primitives, bones, skinIndex, ibmAccessor, animations, embeddedTextures, materialTextureIndex, binaryBytes.Length);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPadding = (4 - (jsonBytes.Length & 3)) & 3;

        int totalLength = 12 + 8 + jsonBytes.Length + jsonPadding + 8 + binaryBytes.Length;

        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        w.Write(MagicGltf);
        w.Write((uint)2);
        w.Write((uint)totalLength);

        w.Write((uint)(jsonBytes.Length + jsonPadding));
        w.Write(ChunkJson);
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPadding; i++)
            w.Write((byte)0x20);

        w.Write((uint)binaryBytes.Length);
        w.Write(ChunkBin);
        w.Write(binaryBytes);
    }

    // ------------------------------------------------------------------------------------------------
    // Mesh primitive assembly (per CirFace)
    // ------------------------------------------------------------------------------------------------

    private static PrimitiveInfo WriteMeshPrimitive(Ctx ctx, CirFace face, CirBoneBindPose[]? bones)
    {
        CirVertex[] vertices = face.Vertices;
        int n = vertices.Length;

        var posBuffer = new byte[n * 12];
        var normalBuffer = new byte[n * 12];
        var uvBuffer = new byte[n * 8];
        byte[]? jointsBuffer = bones is not null ? new byte[n * 4] : null;
        byte[]? weightsBuffer = bones is not null ? new byte[n * 16] : null;

        var min = new float[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var max = new float[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

        for (int i = 0; i < n; i++)
        {
            CirVertex v = vertices[i];
            (Vector3 pos, Vector3 normal) = ResolveVertex(v, bones);

            WriteFloat(posBuffer, i * 12, pos.X);
            WriteFloat(posBuffer, (i * 12) + 4, pos.Y);
            WriteFloat(posBuffer, (i * 12) + 8, pos.Z);
            Track(min, max, pos);

            WriteFloat(normalBuffer, i * 12, normal.X);
            WriteFloat(normalBuffer, (i * 12) + 4, normal.Y);
            WriteFloat(normalBuffer, (i * 12) + 8, normal.Z);

            WriteFloat(uvBuffer, i * 8, v.TextureS);
            WriteFloat(uvBuffer, (i * 8) + 4, v.TextureT);

            if (jointsBuffer is not null && weightsBuffer is not null)
            {
                // CIR's dual-position scheme has at most two joints per vertex; the remaining two slots
                // are unused (joint index 0, weight 0 — glTF-spec-compliant).
                jointsBuffer[i * 4] = (byte)(v.BoneIndex1 & 0xFF);
                jointsBuffer[(i * 4) + 1] = (byte)(v.BoneIndex2 & 0xFF);
                // slot 2, 3 already zero-initialised

                float w = Math.Clamp(v.BoneWeight, 0f, 1f);
                WriteFloat(weightsBuffer, i * 16, w);
                WriteFloat(weightsBuffer, (i * 16) + 4, 1f - w);
                // weights 2, 3 already zero-initialised
            }
        }

        int positionAccessor = ctx.AddAccessor(posBuffer, ComponentFloat, "VEC3", n, TargetArrayBuffer, min, max);
        int normalAccessor = ctx.AddAccessor(normalBuffer, ComponentFloat, "VEC3", n, TargetArrayBuffer);
        int texcoordAccessor = ctx.AddAccessor(uvBuffer, ComponentFloat, "VEC2", n, TargetArrayBuffer);

        int? jointsAccessor = jointsBuffer is not null
            ? ctx.AddAccessor(jointsBuffer, ComponentUByte, "VEC4", n, TargetArrayBuffer)
            : null;
        int? weightsAccessor = weightsBuffer is not null
            ? ctx.AddAccessor(weightsBuffer, ComponentFloat, "VEC4", n, TargetArrayBuffer)
            : null;

        // Indices as UNSIGNED_SHORT when the batch fits, otherwise UNSIGNED_INT. Face batches in TLJ
        // rarely exceed 65k vertices, but we support the fallback so bulk exports don't blow up.
        int indexAccessor = WriteIndices(ctx, face.Triangles, n);

        return new PrimitiveInfo(
            positionAccessor, normalAccessor, texcoordAccessor,
            jointsAccessor, weightsAccessor,
            indexAccessor, face.MaterialIndex);
    }

    private static int WriteIndices(Ctx ctx, CirTriangle[] triangles, int vertexCount)
    {
        int indexCount = triangles.Length * 3;
        bool useShort = vertexCount <= ushort.MaxValue;
        int byteCount = useShort ? indexCount * 2 : indexCount * 4;

        var buffer = new byte[byteCount];
        int p = 0;
        foreach (CirTriangle t in triangles)
        {
            WriteIndex(buffer, ref p, t.VertexIndex1, useShort);
            WriteIndex(buffer, ref p, t.VertexIndex2, useShort);
            WriteIndex(buffer, ref p, t.VertexIndex3, useShort);
        }

        return ctx.AddAccessor(
            buffer,
            useShort ? ComponentUShort : "5125",
            "SCALAR",
            indexCount,
            TargetElementArrayBuffer);
    }

    private static void WriteIndex(byte[] buffer, ref int offset, int value, bool useShort)
    {
        if (useShort)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)value);
            offset += 2;
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)value);
            offset += 4;
        }
    }

    /// <summary>
    /// Blends the two bone-local positions/normals into their bind-pose world equivalents. When no bind
    /// pose is available, falls back to the identity-bone weight-blend the static exporter uses.
    /// </summary>
    private static (Vector3 Pos, Vector3 Normal) ResolveVertex(CirVertex v, CirBoneBindPose[]? bones)
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

        Vector3 finalPos = (wp1 * w) + (wp2 * (1f - w));
        Vector3 finalNormal = (wn1 * w) + (wn2 * (1f - w));
        if (finalNormal.LengthSquared() > 1e-12f)
            finalNormal = Vector3.Normalize(finalNormal);
        return (finalPos, finalNormal);
    }

    private static bool InRange(int i, int n) => i >= 0 && i < n;

    // ------------------------------------------------------------------------------------------------
    // Skinning: inverse bind matrices + skin object + skeleton nodes
    // ------------------------------------------------------------------------------------------------

    private static int WriteInverseBindMatrices(Ctx ctx, CirBoneBindPose[] bones)
    {
        // glTF stores MAT4 as 16 floats in column-major layout. For each bone, we need the transform that
        // takes a point from mesh (world) space to bone-local space at bind time:  local = R^T * (world - t)
        // where (R, t) is the bone's world-space bind pose. That decomposes to
        //     M · p = [R^T | -R^T·t] · p
        // laid out column-major.
        var buffer = new byte[bones.Length * 64];
        for (int i = 0; i < bones.Length; i++)
        {
            CirBoneBindPose b = bones[i];
            Matrix4x4 rot = Matrix4x4.CreateFromQuaternion(b.WorldRotation);
            // System.Numerics stores rotation with M11..M33 as the row-vector rotation matrix. The R^T we
            // want (column-vector convention) is the transpose; column j of R^T is row j of R.
            float[] col0 = [rot.M11, rot.M12, rot.M13, 0f];
            float[] col1 = [rot.M21, rot.M22, rot.M23, 0f];
            float[] col2 = [rot.M31, rot.M32, rot.M33, 0f];
            Vector3 nt = -Vector3.Transform(b.WorldTranslation, Matrix4x4.Transpose(rot));
            float[] col3 = [nt.X, nt.Y, nt.Z, 1f];

            WriteColumn(buffer, i * 64, col0);
            WriteColumn(buffer, (i * 64) + 16, col1);
            WriteColumn(buffer, (i * 64) + 32, col2);
            WriteColumn(buffer, (i * 64) + 48, col3);
        }

        return ctx.AddAccessor(buffer, ComponentFloat, "MAT4", bones.Length);
    }

    private static void WriteColumn(byte[] buffer, int offset, float[] col)
    {
        for (int i = 0; i < 4; i++)
            WriteFloat(buffer, offset + (i * 4), col[i]);
    }

    // ------------------------------------------------------------------------------------------------
    // Animation clips
    // ------------------------------------------------------------------------------------------------

    private sealed record AnimationInfo(string Name, List<SamplerInfo> Samplers, List<ChannelInfo> Channels);
    private sealed record SamplerInfo(int Input, int Output, string Interpolation);
    private sealed record ChannelInfo(int Sampler, int TargetNode, string TargetPath);

    private static AnimationInfo? TryBuildAnimation(Ctx ctx, string name, AniAnimation ani, int boneCount)
    {
        var samplers = new List<SamplerInfo>();
        var channels = new List<ChannelInfo>();

        foreach (AniBoneTrack track in ani.BoneAnims)
        {
            if (track.BoneIndex < 0 || track.BoneIndex >= boneCount || track.Keyframes.Length == 0)
                continue;

            int keyCount = track.Keyframes.Length;
            var timeBuffer = new byte[keyCount * 4];
            var rotBuffer = new byte[keyCount * 16];
            var posBuffer = new byte[keyCount * 12];

            float minTime = float.PositiveInfinity, maxTime = float.NegativeInfinity;
            var prevQuat = new Quaternion(0, 0, 0, 1);
            for (int i = 0; i < keyCount; i++)
            {
                AniKeyframe kf = track.Keyframes[i];
                float tSec = kf.AnimTime / 1000f;
                WriteFloat(timeBuffer, i * 4, tSec);
                if (tSec < minTime) minTime = tSec;
                if (tSec > maxTime) maxTime = tSec;

                var q = new Quaternion(kf.QRotX, kf.QRotY, kf.QRotZ, kf.QRotW);
                if (i > 0 && Quaternion.Dot(q, prevQuat) < 0f)
                    q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                prevQuat = q;

                WriteFloat(rotBuffer, i * 16, q.X);
                WriteFloat(rotBuffer, (i * 16) + 4, q.Y);
                WriteFloat(rotBuffer, (i * 16) + 8, q.Z);
                WriteFloat(rotBuffer, (i * 16) + 12, q.W);

                WriteFloat(posBuffer, i * 12, kf.PosX);
                WriteFloat(posBuffer, (i * 12) + 4, kf.PosY);
                WriteFloat(posBuffer, (i * 12) + 8, kf.PosZ);
            }

            int timeAccessor = ctx.AddAccessor(
                timeBuffer, ComponentFloat, "SCALAR", keyCount,
                target: null,
                min: [minTime], max: [maxTime]);
            int rotAccessor = ctx.AddAccessor(rotBuffer, ComponentFloat, "VEC4", keyCount);
            int posAccessor = ctx.AddAccessor(posBuffer, ComponentFloat, "VEC3", keyCount);

            samplers.Add(new SamplerInfo(timeAccessor, rotAccessor, "LINEAR"));
            channels.Add(new ChannelInfo(samplers.Count - 1, track.BoneIndex, "rotation"));

            samplers.Add(new SamplerInfo(timeAccessor, posAccessor, "LINEAR"));
            channels.Add(new ChannelInfo(samplers.Count - 1, track.BoneIndex, "translation"));
        }

        return channels.Count == 0 ? null : new AnimationInfo(name, samplers, channels);
    }

    // ------------------------------------------------------------------------------------------------
    // JSON assembly
    // ------------------------------------------------------------------------------------------------

    private static string BuildJson(
        CirModel model,
        Ctx ctx,
        List<PrimitiveInfo> primitives,
        CirBoneBindPose[]? bones,
        int? skinIndex,
        int? ibmAccessor,
        List<AnimationInfo> animations,
        List<EmbeddedTextureInfo> embeddedTextures,
        int?[] materialTextureIndex,
        int bufferLength)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"asset\":{\"version\":\"2.0\",\"generator\":\"TLJ Explorer\"}");
        sb.Append(",\"scene\":0");

        // Node table. Skeleton bones (if any) come first — that lets the animation channels target
        // targetNode = boneIndex without an offset.
        int meshNodeIndex = bones?.Length ?? 0;
        var rootBones = new List<int>();
        if (bones is not null)
        {
            for (int i = 0; i < bones.Length; i++)
                if (bones[i].Parent < 0)
                    rootBones.Add(i);
        }

        sb.Append(",\"scenes\":[{\"nodes\":[");
        bool anyRoot = false;
        foreach (int r in rootBones)
        {
            if (anyRoot) sb.Append(',');
            sb.Append(r);
            anyRoot = true;
        }
        if (anyRoot) sb.Append(',');
        sb.Append(meshNodeIndex);
        sb.Append("]}]");

        sb.Append(",\"nodes\":[");
        if (bones is not null)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendBoneNode(sb, i, bones[i]);
            }
            sb.Append(',');
        }

        sb.Append("{\"name\":\"mesh\",\"mesh\":0");
        if (skinIndex is not null)
            sb.Append(",\"skin\":").Append(skinIndex.Value);
        sb.Append('}');
        sb.Append(']');

        // Meshes
        sb.Append(",\"meshes\":[{\"primitives\":[");
        for (int i = 0; i < primitives.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendPrimitive(sb, primitives[i], model);
        }
        sb.Append("]}]");

        // Skin
        if (bones is not null && ibmAccessor is not null)
        {
            sb.Append(",\"skins\":[{\"joints\":[");
            for (int i = 0; i < bones.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(i);
            }
            sb.Append(']');
            if (rootBones.Count > 0)
                sb.Append(",\"skeleton\":").Append(rootBones[0]);
            sb.Append(",\"inverseBindMatrices\":").Append(ibmAccessor.Value);
            sb.Append("}]");
        }

        // Materials
        sb.Append(",\"materials\":[");
        if (model.Materials.Length == 0)
        {
            sb.Append("{\"name\":\"default\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.8,0.8,0.8,1]}}");
        }
        else
        {
            for (int i = 0; i < model.Materials.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendMaterial(sb, model.Materials[i], i, materialTextureIndex[i]);
            }
        }
        sb.Append(']');

        // Embedded textures + sampler (only present when at least one material resolved a texture).
        if (embeddedTextures.Count > 0)
        {
            sb.Append(",\"images\":[");
            for (int i = 0; i < embeddedTextures.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var img = embeddedTextures[i];
                sb.Append("{\"bufferView\":").Append(img.BufferView)
                  .Append(",\"mimeType\":").Append(JsonString(img.MimeType))
                  .Append(",\"name\":").Append(JsonString(img.Name))
                  .Append('}');
            }
            sb.Append(']');

            sb.Append(",\"textures\":[");
            for (int i = 0; i < embeddedTextures.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"source\":").Append(i).Append(",\"sampler\":0}");
            }
            sb.Append(']');

            // Single shared sampler: bilinear filter with linear-mip-linear minification and repeat wrap.
            sb.Append(",\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}]");
        }

        // Animations
        if (animations.Count > 0)
        {
            sb.Append(",\"animations\":[");
            for (int i = 0; i < animations.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendAnimation(sb, animations[i]);
            }
            sb.Append(']');
        }

        // Accessors, bufferViews, buffer
        sb.Append(",\"accessors\":[");
        for (int i = 0; i < ctx.Accessors.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendAccessor(sb, ctx.Accessors[i]);
        }
        sb.Append(']');

        sb.Append(",\"bufferViews\":[");
        for (int i = 0; i < ctx.BufferViews.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var bv = ctx.BufferViews[i];
            sb.Append("{\"buffer\":0,\"byteOffset\":").Append(bv.Offset)
              .Append(",\"byteLength\":").Append(bv.Length);
            if (bv.Target is int target)
                sb.Append(",\"target\":").Append(target);
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append(",\"buffers\":[{\"byteLength\":").Append(bufferLength).Append("}]");
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendBoneNode(StringBuilder sb, int index, CirBoneBindPose bone)
    {
        sb.Append("{\"name\":\"bone_").Append(index.ToString(CultureInfo.InvariantCulture)).Append('"');

        Quaternion r = bone.LocalRotation;
        if (r.X != 0 || r.Y != 0 || r.Z != 0 || r.W != 1)
        {
            sb.Append(",\"rotation\":[")
              .Append(FloatJson(r.X)).Append(',')
              .Append(FloatJson(r.Y)).Append(',')
              .Append(FloatJson(r.Z)).Append(',')
              .Append(FloatJson(r.W)).Append(']');
        }

        Vector3 t = bone.LocalTranslation;
        if (t.X != 0 || t.Y != 0 || t.Z != 0)
        {
            sb.Append(",\"translation\":[")
              .Append(FloatJson(t.X)).Append(',')
              .Append(FloatJson(t.Y)).Append(',')
              .Append(FloatJson(t.Z)).Append(']');
        }

        if (bone.Children.Length > 0)
        {
            sb.Append(",\"children\":[");
            for (int i = 0; i < bone.Children.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(bone.Children[i]);
            }
            sb.Append(']');
        }

        sb.Append('}');
    }

    private static void AppendPrimitive(StringBuilder sb, PrimitiveInfo prim, CirModel model)
    {
        sb.Append("{\"attributes\":{\"POSITION\":").Append(prim.Position)
          .Append(",\"NORMAL\":").Append(prim.Normal)
          .Append(",\"TEXCOORD_0\":").Append(prim.Texcoord);
        if (prim.Joints is int j)
            sb.Append(",\"JOINTS_0\":").Append(j);
        if (prim.Weights is int w)
            sb.Append(",\"WEIGHTS_0\":").Append(w);
        sb.Append('}');
        sb.Append(",\"indices\":").Append(prim.Indices);
        sb.Append(",\"mode\":4"); // TRIANGLES
        if (prim.Material >= 0 && prim.Material < model.Materials.Length)
            sb.Append(",\"material\":").Append(prim.Material);
        else if (model.Materials.Length == 0)
            sb.Append(",\"material\":0");
        sb.Append('}');
    }

    private static void AppendMaterial(StringBuilder sb, CirMaterial m, int index, int? textureIndex)
    {
        sb.Append("{\"name\":").Append(JsonString(string.IsNullOrEmpty(m.Name) ? $"material_{index}" : m.Name));
        sb.Append(",\"pbrMetallicRoughness\":{");
        // When a texture is embedded, don't tint it — the diffuse colour is redundant and would darken
        // the sampled texel. Otherwise use the flat colour so materials without a resolved texture still
        // visually differ.
        if (textureIndex is int tex)
        {
            sb.Append("\"baseColorFactor\":[1,1,1,1]")
              .Append(",\"baseColorTexture\":{\"index\":").Append(tex).Append('}');
        }
        else
        {
            sb.Append("\"baseColorFactor\":[")
              .Append(FloatJson(m.ColourR)).Append(',')
              .Append(FloatJson(m.ColourG)).Append(',')
              .Append(FloatJson(m.ColourB)).Append(",1]");
        }
        sb.Append(",\"metallicFactor\":0,\"roughnessFactor\":0.8}");
        sb.Append('}');
    }

    private static void AppendAnimation(StringBuilder sb, AnimationInfo anim)
    {
        sb.Append("{\"name\":").Append(JsonString(anim.Name));
        sb.Append(",\"samplers\":[");
        for (int i = 0; i < anim.Samplers.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var s = anim.Samplers[i];
            sb.Append("{\"input\":").Append(s.Input)
              .Append(",\"output\":").Append(s.Output)
              .Append(",\"interpolation\":").Append(JsonString(s.Interpolation))
              .Append('}');
        }
        sb.Append(']');
        sb.Append(",\"channels\":[");
        for (int i = 0; i < anim.Channels.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var c = anim.Channels[i];
            sb.Append("{\"sampler\":").Append(c.Sampler)
              .Append(",\"target\":{\"node\":").Append(c.TargetNode)
              .Append(",\"path\":").Append(JsonString(c.TargetPath)).Append('}')
              .Append('}');
        }
        sb.Append(']');
        sb.Append('}');
    }

    private static void AppendAccessor(StringBuilder sb, AccessorInfo a)
    {
        sb.Append("{\"bufferView\":").Append(a.BufferView)
          .Append(",\"componentType\":").Append(a.ComponentType)
          .Append(",\"count\":").Append(a.Count)
          .Append(",\"type\":").Append(JsonString(a.Type));
        if (a.Min is not null)
        {
            sb.Append(",\"min\":[");
            for (int i = 0; i < a.Min.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FloatJson(a.Min[i]));
            }
            sb.Append(']');
        }
        if (a.Max is not null)
        {
            sb.Append(",\"max\":[");
            for (int i = 0; i < a.Max.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FloatJson(a.Max[i]));
            }
            sb.Append(']');
        }
        sb.Append('}');
    }

    // ------------------------------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------------------------------

    private sealed class Ctx
    {
        public MemoryStream Binary { get; } = new();
        public List<BufferViewInfo> BufferViews { get; } = [];
        public List<AccessorInfo> Accessors { get; } = [];

        public int AddAccessor(byte[] data, string componentType, string type, int count, int? target = null, float[]? min = null, float[]? max = null)
        {
            while ((Binary.Length & 3) != 0)
                Binary.WriteByte(0);
            int offset = (int)Binary.Length;
            Binary.Write(data, 0, data.Length);
            BufferViews.Add(new BufferViewInfo(offset, data.Length, target));
            Accessors.Add(new AccessorInfo(BufferViews.Count - 1, componentType, count, type, min, max));
            return Accessors.Count - 1;
        }

        /// <summary>Writes a raw blob into the binary chunk and returns its bufferView index. Used for
        /// embedded PNG/JPEG textures, which reference a bufferView but do not have an accessor.</summary>
        public int AddBufferView(byte[] data)
        {
            while ((Binary.Length & 3) != 0)
                Binary.WriteByte(0);
            int offset = (int)Binary.Length;
            Binary.Write(data, 0, data.Length);
            BufferViews.Add(new BufferViewInfo(offset, data.Length, null));
            return BufferViews.Count - 1;
        }
    }

    private sealed record EmbeddedTextureInfo(int BufferView, string MimeType, string Name);

    /// <summary>Detects the PNG magic (89 50 4E 47 0D 0A 1A 0A) so we can pick the right mime type without
    /// forcing the caller to hand it to us. Anything that isn't PNG is assumed to be JPEG.</summary>
    private static bool LooksLikePng(byte[] data) =>
        data.Length >= 8 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    private sealed record BufferViewInfo(int Offset, int Length, int? Target);

    private sealed record AccessorInfo(int BufferView, string ComponentType, int Count, string Type, float[]? Min, float[]? Max);

    private sealed record PrimitiveInfo(
        int Position, int Normal, int Texcoord,
        int? Joints, int? Weights,
        int Indices, int Material);

    private static void WriteFloat(byte[] buffer, int offset, float value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), value);

    private static void Track(float[] min, float[] max, Vector3 v)
    {
        if (v.X < min[0]) min[0] = v.X; if (v.X > max[0]) max[0] = v.X;
        if (v.Y < min[1]) min[1] = v.Y; if (v.Y > max[1]) max[1] = v.Y;
        if (v.Z < min[2]) min[2] = v.Z; if (v.Z > max[2]) max[2] = v.Z;
    }

    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string FloatJson(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return "0";
        return value.ToString("G7", CultureInfo.InvariantCulture);
    }
}
