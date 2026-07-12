using System.Globalization;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Decoder for the CIR 3D skeletal model format: materials, a bone skeleton, and one or more mesh
/// groups whose vertices use a dual-position two-bone-blend skinning scheme.
/// </summary>
/// <remarks>
/// <para>All strings in this format are length-prefixed: <c>UInt32 Length</c> followed by that many raw
/// bytes interpreted as ASCII, with no null terminator. All arrays are length-prefixed the same way:
/// <c>Int32 Count</c> followed by that many entries.</para>
/// <para>File header (little-endian):</para>
/// <code>
/// Int32  Id           must equal 4
/// Int32  Version      must be 16 or 256
/// Int32  Unknown1     -- ONLY present when Version == 256
/// Int32  Unknown2
/// Single Unknown3
/// </code>
/// <para>Body:</para>
/// <code>
/// Materials: array of {
///     string Name
///     Int32  Unknown1
///     string TextureName
///     Single ColourR, ColourG, ColourB
/// }
///
/// Unknown4: array of { Single[4] } -- purpose unknown, likely bounding info
///
/// Skeleton: array of bones {
///     string Name
///     Single Unknown1
///     Children: array of Int32 -- indices back into the Skeleton array
/// }
///
/// Groups: array of {
///     string Name
///     Faces: array of {
///         Int32 MaterialIndex
///         Vertices: array of {
///             Single PosX1, PosY1, PosZ1     -- position in bone reference frame 1
///             Single PosX2, PosY2, PosZ2     -- SAME vertex, position in bone reference frame 2
///             Single NormalX, NormalY, NormalZ
///             Single TextureS, TextureT
///             Int32  BoneIndex1, BoneIndex2
///             Single BoneWeight              -- weight for frame1; frame2 uses (1 - BoneWeight)
///         }
///         Triangles: array of { Int32 VertexIndex1, VertexIndex2, VertexIndex3 }
///     }
///     Unknown1: array of { Single[4], Int32 }
///     Unknown2: array of { string Unknown2_01, Single[8], Int32 }
/// }
/// </code>
/// <para>Only <c>Groups[0]</c> is actually used/rendered by the game; further groups (if any) are kept
/// for fidelity but not expected to be needed by a renderer.</para>
/// </remarks>
public static class CirDecoder
{
    private const int ExpectedId = 4;

    /// <summary>Reads a full CIR model from <paramref name="stream"/>.</summary>
    /// <exception cref="FormatException">The stream does not contain a recognizable CIR model.</exception>
    public static CirModel Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        int id = reader.ReadInt32();
        if (id != ExpectedId)
        {
            throw new FormatException($"CIR file has unexpected Id {id}; expected {ExpectedId}.");
        }

        int version = reader.ReadInt32();
        if (version is not (16 or 256))
        {
            throw new FormatException($"Unsupported CIR version {version}; expected 16 or 256.");
        }

        if (version == 256)
        {
            _ = reader.ReadInt32(); // Unknown1 (only present in version 256)
        }

        uint magic = reader.ReadUInt32();
        if (magic != 0xDEADBABE)
        {
            throw new FormatException(
                $"CIR file missing 0xDEADBABE sentinel (found 0x{magic:X8}); stream is misaligned or corrupt.");
        }

        _ = reader.ReadSingle(); // Unknown3

        var materials = ReadArray(reader, ReadMaterial);
        var unknown4 = ReadArray(reader, r => new CirUnknown4Entry(ReadFloats(r, 4)));
        var skeleton = ReadArray(reader, ReadBone);
        var groups = ReadArray(reader, ReadGroup);

        return new CirModel
        {
            Version = version,
            Materials = materials,
            Unknown4 = unknown4,
            Skeleton = skeleton,
            Groups = groups,
        };
    }

    private static CirMaterial ReadMaterial(BinaryReader reader)
    {
        string name = ReadString(reader);
        int unknown1 = reader.ReadInt32();
        string textureName = ReadString(reader);
        float colourR = reader.ReadSingle();
        float colourG = reader.ReadSingle();
        float colourB = reader.ReadSingle();
        return new CirMaterial(name, unknown1, textureName, colourR, colourG, colourB);
    }

    private static CirBone ReadBone(BinaryReader reader)
    {
        string name = ReadString(reader);
        float unknown1 = reader.ReadSingle();
        var children = ReadArray(reader, r => r.ReadInt32());
        return new CirBone(name, unknown1, children);
    }

    private static CirGroup ReadGroup(BinaryReader reader)
    {
        string name = ReadString(reader);
        var faces = ReadArray(reader, ReadFace);
        var unknown1 = ReadArray(reader, ReadGroupUnknown1Entry);
        var unknown2 = ReadArray(reader, ReadGroupUnknown2Entry);
        return new CirGroup(name, faces, unknown1, unknown2);
    }

    private static CirFace ReadFace(BinaryReader reader)
    {
        int materialIndex = reader.ReadInt32();
        var vertices = ReadArray(reader, ReadVertex);
        var triangles = ReadArray(reader, ReadTriangle);
        return new CirFace(materialIndex, vertices, triangles);
    }

    private static CirVertex ReadVertex(BinaryReader reader)
    {
        float posX1 = reader.ReadSingle();
        float posY1 = reader.ReadSingle();
        float posZ1 = reader.ReadSingle();
        float posX2 = reader.ReadSingle();
        float posY2 = reader.ReadSingle();
        float posZ2 = reader.ReadSingle();
        float normalX = reader.ReadSingle();
        float normalY = reader.ReadSingle();
        float normalZ = reader.ReadSingle();
        float textureS = reader.ReadSingle();
        float textureT = reader.ReadSingle();
        int boneIndex1 = reader.ReadInt32();
        int boneIndex2 = reader.ReadInt32();
        float boneWeight = reader.ReadSingle();
        return new CirVertex(
            posX1, posY1, posZ1,
            posX2, posY2, posZ2,
            normalX, normalY, normalZ,
            textureS, textureT,
            boneIndex1, boneIndex2, boneWeight);
    }

    private static CirTriangle ReadTriangle(BinaryReader reader)
    {
        int v1 = reader.ReadInt32();
        int v2 = reader.ReadInt32();
        int v3 = reader.ReadInt32();
        return new CirTriangle(v1, v2, v3);
    }

    private static CirGroupUnknown1Entry ReadGroupUnknown1Entry(BinaryReader reader)
    {
        float v1 = reader.ReadSingle();
        float v2 = reader.ReadSingle();
        float v3 = reader.ReadSingle();
        float v4 = reader.ReadSingle();
        int v5 = reader.ReadInt32();
        return new CirGroupUnknown1Entry(v1, v2, v3, v4, v5);
    }

    private static CirGroupUnknown2Entry ReadGroupUnknown2Entry(BinaryReader reader)
    {
        string unknown2_01 = ReadString(reader);
        float v1 = reader.ReadSingle();
        float v2 = reader.ReadSingle();
        float v3 = reader.ReadSingle();
        float v4 = reader.ReadSingle();
        float v5 = reader.ReadSingle();
        float v6 = reader.ReadSingle();
        float v7 = reader.ReadSingle();
        float v8 = reader.ReadSingle();
        int v9 = reader.ReadInt32();
        return new CirGroupUnknown2Entry(unknown2_01, v1, v2, v3, v4, v5, v6, v7, v8, v9);
    }

    private static float[] ReadFloats(BinaryReader reader, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadSingle();
        }

        return values;
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = checked((int)reader.ReadUInt32());
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = reader.ReadBytes(length);
        return Encoding.ASCII.GetString(bytes);
    }

    private static T[] ReadArray<T>(BinaryReader reader, Func<BinaryReader, T> readEntry)
    {
        int count = reader.ReadInt32();
        var items = new T[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = readEntry(reader);
        }

        return items;
    }

    /// <summary>
    /// Produces an indented, human-readable text dump of the whole model structure, useful as a
    /// debug/inspection view.
    /// </summary>
    public static string DumpAsText(CirModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var sb = new StringBuilder();
        var w = new TextDumpWriter(sb);

        w.BeginObject("CirModel");
        w.Field("Version", model.Version);

        w.BeginArrayField("Materials", model.Materials.Length);
        foreach (var m in model.Materials)
        {
            w.BeginObject();
            w.Field("Name", m.Name);
            w.Field("Unknown1", m.Unknown1);
            w.Field("TextureName", m.TextureName);
            w.Field("ColourR", m.ColourR);
            w.Field("ColourG", m.ColourG);
            w.Field("ColourB", m.ColourB);
            w.EndObject();
        }

        w.EndArray();

        w.BeginArrayField("Unknown4", model.Unknown4.Length);
        foreach (var u in model.Unknown4)
        {
            w.FloatArrayItem(u.Values);
        }

        w.EndArray();

        w.BeginArrayField("Skeleton", model.Skeleton.Length);
        foreach (var bone in model.Skeleton)
        {
            w.BeginObject();
            w.Field("Name", bone.Name);
            w.Field("Unknown1", bone.Unknown1);
            w.IntArrayField("Children", bone.Children);
            w.EndObject();
        }

        w.EndArray();

        w.BeginArrayField("Groups", model.Groups.Length);
        foreach (var group in model.Groups)
        {
            w.BeginObject();
            w.Field("Name", group.Name);

            w.BeginArrayField("Faces", group.Faces.Length);
            foreach (var face in group.Faces)
            {
                w.BeginObject();
                w.Field("MaterialIndex", face.MaterialIndex);

                w.BeginArrayField("Vertices", face.Vertices.Length);
                foreach (var v in face.Vertices)
                {
                    w.BeginObject();
                    w.Field("PosX1", v.PosX1);
                    w.Field("PosY1", v.PosY1);
                    w.Field("PosZ1", v.PosZ1);
                    w.Field("PosX2", v.PosX2);
                    w.Field("PosY2", v.PosY2);
                    w.Field("PosZ2", v.PosZ2);
                    w.Field("NormalX", v.NormalX);
                    w.Field("NormalY", v.NormalY);
                    w.Field("NormalZ", v.NormalZ);
                    w.Field("TextureS", v.TextureS);
                    w.Field("TextureT", v.TextureT);
                    w.Field("BoneIndex1", v.BoneIndex1);
                    w.Field("BoneIndex2", v.BoneIndex2);
                    w.Field("BoneWeight", v.BoneWeight);
                    w.EndObject();
                }

                w.EndArray();

                w.BeginArrayField("Triangles", face.Triangles.Length);
                foreach (var t in face.Triangles)
                {
                    w.BeginObject();
                    w.Field("VertexIndex1", t.VertexIndex1);
                    w.Field("VertexIndex2", t.VertexIndex2);
                    w.Field("VertexIndex3", t.VertexIndex3);
                    w.EndObject();
                }

                w.EndArray();
                w.EndObject();
            }

            w.EndArray();

            w.BeginArrayField("Unknown1", group.Unknown1.Length);
            foreach (var u in group.Unknown1)
            {
                w.BeginObject();
                w.Field("Value1", u.Value1);
                w.Field("Value2", u.Value2);
                w.Field("Value3", u.Value3);
                w.Field("Value4", u.Value4);
                w.Field("Value5", u.Value5);
                w.EndObject();
            }

            w.EndArray();

            w.BeginArrayField("Unknown2", group.Unknown2.Length);
            foreach (var u in group.Unknown2)
            {
                w.BeginObject();
                w.Field("Unknown2_01", u.Unknown2_01);
                w.Field("Value1", u.Value1);
                w.Field("Value2", u.Value2);
                w.Field("Value3", u.Value3);
                w.Field("Value4", u.Value4);
                w.Field("Value5", u.Value5);
                w.Field("Value6", u.Value6);
                w.Field("Value7", u.Value7);
                w.Field("Value8", u.Value8);
                w.Field("Value9", u.Value9);
                w.EndObject();
            }

            w.EndArray();
            w.EndObject();
        }

        w.EndArray();
        w.EndObject();

        return sb.ToString();
    }

    /// <summary>
    /// Minimal indenting writer shared by <see cref="CirDecoder"/> and <see cref="AniDecoder"/> for
    /// producing a pretty-printed pseudo-JSON debug dump.
    /// </summary>
    internal sealed class TextDumpWriter(StringBuilder sb)
    {
        private int _indent;

        public void BeginObject(string? name = null)
        {
            WritePrefix(name);
            sb.Append('{').Append('\n');
            _indent++;
        }

        public void EndObject()
        {
            _indent--;
            AppendIndent();
            sb.Append('}').Append('\n');
        }

        public void BeginArrayField(string name, int count)
        {
            WritePrefix(name);
            sb.Append('[').Append(" // count=").Append(count).Append('\n');
            _indent++;
        }

        public void EndArray()
        {
            _indent--;
            AppendIndent();
            sb.Append(']').Append('\n');
        }

        public void Field(string name, string value)
        {
            AppendIndent();
            sb.Append(name).Append(": \"").Append(value).Append('"').Append('\n');
        }

        public void Field(string name, int value)
        {
            AppendIndent();
            sb.Append(name).Append(": ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        public void Field(string name, float value)
        {
            AppendIndent();
            sb.Append(name).Append(": ").Append(FormatFloat(value)).Append('\n');
        }

        public void IntArrayField(string name, int[] values)
        {
            AppendIndent();
            sb.Append(name).Append(": [");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(']').Append('\n');
        }

        public void FloatArrayItem(float[] values)
        {
            AppendIndent();
            sb.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(FormatFloat(values[i]));
            }

            sb.Append(']').Append('\n');
        }

        private void WritePrefix(string? name)
        {
            AppendIndent();
            if (name is not null)
            {
                sb.Append(name).Append(": ");
            }
        }

        private void AppendIndent()
        {
            sb.Append(' ', _indent * 2);
        }

        internal static string FormatFloat(float value)
        {
            return value.ToString("G7", CultureInfo.InvariantCulture);
        }
    }
}
