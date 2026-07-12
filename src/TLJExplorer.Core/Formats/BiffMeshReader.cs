using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Reads a BIFF prop-mesh archive into a typed <see cref="BiffMesh"/>. Structurally identical to
/// <see cref="BiffDump"/> but returns typed data instead of text, so callers (renderer, glTF exporter,
/// tests) can consume the geometry directly. Follows ScummVM's <c>engines/stark/formats/biffmesh.cpp</c>
/// exactly.
/// </summary>
public static class BiffMeshReader
{
    private const uint TypeSceneData = 0x05a4aa94;
    private const uint TypeBase      = 0x05a4aa89;
    private const uint TypeTri       = 0x05a4aa8d;
    private const uint TypeMaterial  = 0x05a4aa8e;

    /// <summary>
    /// Reads a BIFF archive from <paramref name="stream"/> and returns the single mesh it contains.
    /// </summary>
    /// <exception cref="FormatException">
    /// The stream is not a BIFF archive, uses an unsupported version, or the archive does not contain
    /// exactly one <c>MeshObjectTri</c> chunk.
    /// </exception>
    public static BiffMesh Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || Encoding.ASCII.GetString(magic) != "BIFF")
            throw new FormatException("Not a BIFF file (bad magic).");

        uint fileVersion = reader.ReadUInt32();
        if (fileVersion is not (1 or 2))
            throw new FormatException($"Unsupported BIFF version {fileVersion} (expected 1 or 2).");

        _ = reader.ReadUInt32(); // Unknown1
        _ = reader.ReadUInt32(); // Unknown2
        uint numBlocks = reader.ReadUInt32();

        var context = new ReadContext(fileVersion);
        for (uint i = 0; i < numBlocks; i++)
        {
            ReadBlock(reader, fileVersion, context);
        }

        if (context.Tris.Count != 1)
            throw new FormatException(
                $"BIFF archive is expected to contain exactly one MeshObjectTri chunk; found {context.Tris.Count}.");

        RawTri tri = context.Tris[0];

        return new BiffMesh
        {
            AnimStart = context.AnimStart,
            AnimEnd = context.AnimEnd,
            Name = tri.Name,
            KeyFrames = tri.KeyFrames.ToArray(),
            Materials = context.Materials.ToArray(),
            Vertices = tri.ReindexedVertices,
            Groups = tri.Groups,
            HasPhysics = tri.HasPhysics,
        };
    }

    private sealed class ReadContext(uint fileVersion)
    {
        public uint FileVersion { get; } = fileVersion;
        public uint? AnimStart { get; set; }
        public uint? AnimEnd { get; set; }
        public List<BiffMaterial> Materials { get; } = [];
        public List<RawTri> Tris { get; } = [];
    }

    private sealed class RawTri
    {
        public string Name = string.Empty;
        public readonly List<BiffKeyFrame> KeyFrames = [];
        public BiffReindexedVertex[] ReindexedVertices = [];
        public BiffMeshGroup[] Groups = [];
        public bool HasPhysics;
    }

    private static void ReadBlock(BinaryReader reader, uint fileVersion, ReadContext ctx)
    {
        _ = reader.ReadUInt32(); // BeginMarker
        uint typeId = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // Unknown1
        int dataSize = reader.ReadInt32();
        uint objectVersion = 0;
        if (fileVersion == 2)
            objectVersion = reader.ReadUInt32();

        long payloadStart = reader.BaseStream.Position;

        switch (typeId)
        {
            case TypeSceneData:
                ctx.AnimStart = reader.ReadUInt32();
                ctx.AnimEnd = reader.ReadUInt32();
                break;

            case TypeBase:
                _ = ReadString16(reader); // name — informational only
                break;

            case TypeTri:
                ctx.Tris.Add(ReadTri(reader, objectVersion));
                break;

            case TypeMaterial:
                ctx.Materials.Add(ReadMaterial(reader));
                break;

            default:
                // Unknown chunk: skip its body.
                break;
        }

        long consumed = reader.BaseStream.Position - payloadStart;
        long remaining = dataSize - consumed;
        if (remaining > 0)
            reader.BaseStream.Seek(remaining, SeekOrigin.Current);
        else if (remaining < 0)
            throw new FormatException($"BIFF block payload over-read for type 0x{typeId:X8} (over by {-remaining} bytes).");

        _ = reader.ReadUInt32(); // EndMarker
        uint numSubBlocks = reader.ReadUInt32();
        for (uint i = 0; i < numSubBlocks; i++)
            ReadBlock(reader, fileVersion, ctx);
    }

    private static RawTri ReadTri(BinaryReader reader, uint objectVersion)
    {
        var tri = new RawTri { Name = ReadString16(reader) };

        uint keyFrameCount = reader.ReadUInt32();
        for (uint i = 0; i < keyFrameCount; i++)
        {
            tri.KeyFrames.Add(new BiffKeyFrame(
                Time: reader.ReadUInt32(),
                EssentialRotation: ReadQuat(reader),
                Determinant: reader.ReadSingle(),
                StretchRotation: ReadQuat(reader),
                Scale: ReadVec3(reader),
                Translation: ReadVec3(reader)));
        }

        if (objectVersion >= 2)
        {
            uint uvKeyFrameCount = reader.ReadUInt32();
            uint attributeCount = reader.ReadUInt32();
            if (uvKeyFrameCount != 0 || attributeCount != 0)
            {
                throw new FormatException(
                    "BIFF MeshObjectTri: reading uv-keyframes / attributes is not implemented "
                    + $"(uvKeyFrameCount={uvKeyFrameCount}, attributeCount={attributeCount}).");
            }
        }

        uint vertexCount = reader.ReadUInt32();
        var rawVertices = new (float X, float Y, float Z)[vertexCount];
        for (uint i = 0; i < vertexCount; i++)
        {
            _ = ReadString16(reader); // animName1
            _ = ReadString16(reader); // animName2
            _ = reader.ReadSingle(); // animInfluence1
            _ = reader.ReadSingle(); // animInfluence2
            rawVertices[i] = ReadVec3(reader);
        }

        uint normalCount = reader.ReadUInt32();
        var rawNormals = new (float X, float Y, float Z)[normalCount];
        for (uint i = 0; i < normalCount; i++)
            rawNormals[i] = ReadVec3(reader);

        uint textureVertexCount = reader.ReadUInt32();
        var rawTexCoords = new (float U, float V)[textureVertexCount];
        for (uint i = 0; i < textureVertexCount; i++)
        {
            var t = ReadVec3(reader);
            rawTexCoords[i] = (t.X, t.Y); // z is unused per ScummVM
        }

        uint faceCount = reader.ReadUInt32();
        var rawFaces = new BiffFace[faceCount];
        for (uint i = 0; i < faceCount; i++)
        {
            rawFaces[i] = new BiffFace(
                V0: (int)reader.ReadUInt32(), V1: (int)reader.ReadUInt32(), V2: (int)reader.ReadUInt32(),
                N0: (int)reader.ReadUInt32(), N1: (int)reader.ReadUInt32(), N2: (int)reader.ReadUInt32(),
                T0: (int)reader.ReadUInt32(), T1: (int)reader.ReadUInt32(), T2: (int)reader.ReadUInt32(),
                MaterialId: reader.ReadUInt32(),
                SmoothingGroup: reader.ReadUInt32());
        }

        tri.HasPhysics = reader.ReadByte() != 0;

        Reindex(rawVertices, rawNormals, rawTexCoords, rawFaces, out tri.ReindexedVertices, out tri.Groups);

        return tri;
    }

    /// <summary>
    /// Collapses the raw multi-indexed layout into a single per-vertex-index scheme grouped by material.
    /// Mirrors <c>MeshObjectTri::reindex()</c> in ScummVM.
    /// </summary>
    private static void Reindex(
        (float X, float Y, float Z)[] rawVertices,
        (float X, float Y, float Z)[] rawNormals,
        (float U, float V)[] rawTexCoords,
        BiffFace[] rawFaces,
        out BiffReindexedVertex[] outVertices,
        out BiffMeshGroup[] outGroups)
    {
        var vertexIndexMap = new Dictionary<(int V, int N, int T), int>(rawFaces.Length * 3);
        var vertices = new List<BiffReindexedVertex>(rawFaces.Length * 2);
        var groupBuilders = new Dictionary<uint, List<int>>();

        for (int i = 0; i < rawFaces.Length; i++)
        {
            BiffFace face = rawFaces[i];
            uint materialId = face.MaterialId;
            if (!groupBuilders.TryGetValue(materialId, out List<int>? indices))
            {
                indices = [];
                groupBuilders[materialId] = indices;
            }

            AddIndex(face.V0, face.N0, face.T0);
            AddIndex(face.V1, face.N1, face.T1);
            AddIndex(face.V2, face.N2, face.T2);

            void AddIndex(int v, int n, int t)
            {
                var key = (v, n, t);
                if (!vertexIndexMap.TryGetValue(key, out int idx))
                {
                    idx = vertices.Count;
                    vertexIndexMap[key] = idx;

                    (float X, float Y, float Z) p = IndexOrDefault(rawVertices, v);
                    (float X, float Y, float Z) nn = IndexOrDefault(rawNormals, n);
                    (float U, float V) uv = IndexOrDefault(rawTexCoords, t);
                    vertices.Add(new BiffReindexedVertex(p, nn, uv));
                }

                indices!.Add(idx);
            }
        }

        outVertices = vertices.ToArray();
        outGroups = groupBuilders
            .OrderBy(kv => kv.Key)
            .Select(kv => new BiffMeshGroup(kv.Key, kv.Value.ToArray()))
            .ToArray();
    }

    private static T IndexOrDefault<T>(T[] array, int index) where T : struct =>
        index >= 0 && index < array.Length ? array[index] : default;

    private static BiffMaterial ReadMaterial(BinaryReader reader)
    {
        string name = ReadString16(reader);
        string texture = ReadString16(reader);
        string alpha = ReadString16(reader);
        string environment = ReadString16(reader);

        uint shading = reader.ReadUInt32();
        var ambient = ReadVec3(reader);
        var diffuse = ReadVec3(reader);
        var specular = ReadVec3(reader);

        float shininess = reader.ReadSingle();
        float opacity = reader.ReadSingle();

        bool doubleSided = reader.ReadByte() != 0;
        uint textureTiling = reader.ReadUInt32();
        uint alphaTiling = reader.ReadUInt32();
        uint environmentTiling = reader.ReadUInt32();

        bool isColorKey = reader.ReadByte() != 0;
        uint colorKey = reader.ReadUInt32();

        uint attributeCount = reader.ReadUInt32();
        if (attributeCount != 0)
        {
            throw new FormatException(
                $"BIFF MeshObjectMaterial: reading {attributeCount} material attributes is not implemented.");
        }

        return new BiffMaterial(
            name, texture, alpha, environment, shading,
            ambient, diffuse, specular,
            shininess, opacity,
            doubleSided, textureTiling, alphaTiling, environmentTiling,
            isColorKey, colorKey);
    }

    private static string ReadString16(BinaryReader reader)
    {
        ushort length = reader.ReadUInt16();
        return length == 0 ? string.Empty : Encoding.ASCII.GetString(reader.ReadBytes(length));
    }

    private static (float X, float Y, float Z) ReadVec3(BinaryReader reader) =>
        (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static (float X, float Y, float Z, float W) ReadQuat(BinaryReader reader) =>
        (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
