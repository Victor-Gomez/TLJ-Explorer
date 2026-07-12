using System.Globalization;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Text dumper for the generic "BIFF" block container format.
/// </summary>
/// <remarks>
/// BIFF files share the same outer block/trailer framing as the TM texture container (see
/// <c>TmDecoder.cs</c>), but use a different set of block type IDs and, unlike TmDecoder, this class
/// never decodes pixel/palette data — it only produces a human-readable text description of the
/// block tree, which is useful for inspecting BIFF-based resources whose block types aren't (yet)
/// otherwise understood.
///
/// File layout:
/// <code>
/// char[4] Id        must be ASCII "BIFF"
/// UInt32  Version    1 or 2
/// UInt32  Unknown1
/// UInt32  Unknown2
/// UInt32  NumBlocks
/// &lt;NumBlocks top-level blocks, recursively&gt;
/// </code>
///
/// Each block is framed as:
/// <code>
/// UInt32 BeginMarker
/// UInt32 TypeId
/// UInt32 Unknown1
/// Int32  DataSize
/// UInt32 ObjectVersion   -- only present when the file's Version == 2; per-object payload version
/// byte[DataSize] Data    -- interpreted according to TypeId
/// UInt32 EndMarker
/// UInt32 NumSubBlocks
/// &lt;NumSubBlocks nested blocks, recursively&gt;
/// </code>
/// </remarks>
public static class BiffDump
{
    private const uint TypeImage = 0x02faf080;
    private const uint TypePalette = 0x02faf082;
    private const uint TypeSceneData = 0x05a4aa94;
    private const uint TypeLister = 0x05a4aa89;
    private const uint TypeObject = 0x05a4aa8d;
    private const uint TypeMaterial = 0x05a4aa8e;

    /// <summary>
    /// Reads a BIFF container from <paramref name="stream"/> and returns a text tree describing its blocks.
    /// </summary>
    public static string DumpAsText(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var idBytes = reader.ReadBytes(4);
        if (idBytes.Length != 4 || Encoding.ASCII.GetString(idBytes) != "BIFF")
            throw new FormatException("Not a BIFF file (bad magic).");

        uint version = reader.ReadUInt32();
        if (version is not (1 or 2))
            throw new FormatException($"Unsupported BIFF version {version} (expected 1 or 2).");

        _ = reader.ReadUInt32(); // Unknown1
        _ = reader.ReadUInt32(); // Unknown2
        uint numBlocks = reader.ReadUInt32();

        var sb = new StringBuilder();
        for (uint i = 0; i < numBlocks; i++)
        {
            DumpBlock(reader, version, sb, indent: 0);
        }

        return sb.ToString();
    }

    private static void DumpBlock(BinaryReader reader, uint fileVersion, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);

        _ = reader.ReadUInt32(); // BeginMarker
        uint typeId = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // Unknown1
        int dataSize = reader.ReadInt32();
        uint objectVersion = 0;
        if (fileVersion == 2)
            objectVersion = reader.ReadUInt32();

        long payloadStart = reader.BaseStream.Position;

        sb.Append(pad).Append(BlockName(typeId)).Append(" {\n");
        DumpPayload(reader, typeId, dataSize, objectVersion, sb, indent + 1);

        long consumed = reader.BaseStream.Position - payloadStart;
        long remaining = dataSize - consumed;
        if (remaining > 0)
        {
            reader.BaseStream.Seek(remaining, SeekOrigin.Current);
        }
        else if (remaining < 0)
        {
            // Payload decoder over-read; nothing sensible to do but leave the stream where it is.
        }

        _ = reader.ReadUInt32(); // EndMarker
        uint numSubBlocks = reader.ReadUInt32();

        for (uint i = 0; i < numSubBlocks; i++)
        {
            DumpBlock(reader, fileVersion, sb, indent + 1);
        }

        sb.Append(pad).Append("}\n");
    }

    private static string BlockName(uint typeId) => typeId switch
    {
        TypeImage => "Image",
        TypePalette => "Palette",
        TypeSceneData => "SceneData",
        TypeLister => "Lister",
        TypeObject => "MeshObjectTri",
        TypeMaterial => "Material",
        _ => $"Block 0x{typeId:X8}",
    };

    private static void DumpPayload(BinaryReader reader, uint typeId, int dataSize, uint objectVersion, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);
        long payloadStart = reader.BaseStream.Position;

        switch (typeId)
        {
            case TypeImage:
                sb.Append(pad).Append("[image data]\n");
                reader.BaseStream.Seek(dataSize, SeekOrigin.Current);
                break;

            case TypePalette:
                sb.Append(pad).Append("[palette data]\n");
                reader.BaseStream.Seek(dataSize, SeekOrigin.Current);
                break;

            case TypeSceneData:
            {
                uint animStart = reader.ReadUInt32();
                uint animEnd = reader.ReadUInt32();
                sb.Append(pad).Append("animStart=").Append(animStart.ToString(CultureInfo.InvariantCulture))
                    .Append(" animEnd=").Append(animEnd.ToString(CultureInfo.InvariantCulture)).Append('\n');
                AppendLeftoverNote(reader, payloadStart, dataSize, sb, pad);
                break;
            }

            case TypeLister:
            {
                string name = ReadString16(reader);
                sb.Append(pad).Append("Lister: \"").Append(name).Append("\"\n");
                AppendLeftoverNote(reader, payloadStart, dataSize, sb, pad);
                break;
            }

            case TypeObject:
                DumpMeshObjectTri(reader, dataSize, objectVersion, sb, pad, indent);
                break;

            case TypeMaterial:
                DumpMaterial(reader, dataSize, sb, pad);
                break;

            default:
                sb.Append(pad).Append("Unknown chunk type 0x").Append(typeId.ToString("X8", CultureInfo.InvariantCulture))
                    .Append(" (").Append(dataSize.ToString(CultureInfo.InvariantCulture)).Append(" bytes)\n");
                reader.BaseStream.Seek(dataSize, SeekOrigin.Current);
                break;
        }
    }

    private static void DumpMeshObjectTri(BinaryReader reader, int dataSize, uint objectVersion, StringBuilder sb, string pad, int indent)
    {
        long payloadStart = reader.BaseStream.Position;
        string innerPad = new(' ', (indent + 1) * 2);

        string name = ReadString16(reader);
        sb.Append(pad).Append("name=\"").Append(name).Append("\"\n");

        uint keyFrameCount = reader.ReadUInt32();
        sb.Append(pad).Append("keyFrames (").Append(keyFrameCount.ToString(CultureInfo.InvariantCulture)).Append(") [\n");
        for (uint i = 0; i < keyFrameCount; i++)
        {
            uint time = reader.ReadUInt32();
            var essentialRot = ReadQuaternion(reader);
            float determinant = reader.ReadSingle();
            var stretchRot = ReadQuaternion(reader);
            var scale = ReadVector3(reader);
            var translation = ReadVector3(reader);

            sb.Append(innerPad).Append("time=").Append(time.ToString(CultureInfo.InvariantCulture))
                .Append(" essentialRot=").Append(FormatQuat(essentialRot))
                .Append(" det=").Append(FormatFloat(determinant))
                .Append(" stretchRot=").Append(FormatQuat(stretchRot))
                .Append(" scale=").Append(FormatVec3(scale))
                .Append(" translation=").Append(FormatVec3(translation))
                .Append('\n');
        }

        sb.Append(pad).Append("]\n");

        if (objectVersion >= 2)
        {
            uint uvKeyFrameCount = reader.ReadUInt32();
            uint attributeCount = reader.ReadUInt32();
            sb.Append(pad).Append("uvKeyFrameCount=").Append(uvKeyFrameCount.ToString(CultureInfo.InvariantCulture))
                .Append(" attributeCount=").Append(attributeCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (uvKeyFrameCount != 0 || attributeCount != 0)
            {
                sb.Append(pad).Append("[uv-keyframe / attribute payloads are not decoded; giving up on this chunk]\n");
                AppendLeftoverNote(reader, payloadStart, dataSize, sb, pad);
                return;
            }
        }

        uint vertexCount = reader.ReadUInt32();
        sb.Append(pad).Append("vertices (").Append(vertexCount.ToString(CultureInfo.InvariantCulture)).Append(") [\n");
        for (uint i = 0; i < vertexCount; i++)
        {
            string animName1 = ReadString16(reader);
            string animName2 = ReadString16(reader);
            float animInfluence1 = reader.ReadSingle();
            float animInfluence2 = reader.ReadSingle();
            var position = ReadVector3(reader);

            sb.Append(innerPad).Append("anim=(\"").Append(animName1).Append("\", \"").Append(animName2)
                .Append("\") w=(").Append(FormatFloat(animInfluence1)).Append(", ").Append(FormatFloat(animInfluence2))
                .Append(") pos=").Append(FormatVec3(position)).Append('\n');
        }

        sb.Append(pad).Append("]\n");

        uint normalCount = reader.ReadUInt32();
        sb.Append(pad).Append("normals (").Append(normalCount.ToString(CultureInfo.InvariantCulture)).Append(") [\n");
        for (uint i = 0; i < normalCount; i++)
        {
            var n = ReadVector3(reader);
            sb.Append(innerPad).Append(FormatVec3(n)).Append('\n');
        }

        sb.Append(pad).Append("]\n");

        uint textureVertexCount = reader.ReadUInt32();
        sb.Append(pad).Append("texturePositions (").Append(textureVertexCount.ToString(CultureInfo.InvariantCulture)).Append(") [\n");
        for (uint i = 0; i < textureVertexCount; i++)
        {
            var t = ReadVector3(reader);
            sb.Append(innerPad).Append(FormatVec3(t)).Append('\n');
        }

        sb.Append(pad).Append("]\n");

        uint faceCount = reader.ReadUInt32();
        sb.Append(pad).Append("faces (").Append(faceCount.ToString(CultureInfo.InvariantCulture)).Append(") [\n");
        for (uint i = 0; i < faceCount; i++)
        {
            uint v0 = reader.ReadUInt32(), v1 = reader.ReadUInt32(), v2 = reader.ReadUInt32();
            uint n0 = reader.ReadUInt32(), n1 = reader.ReadUInt32(), n2 = reader.ReadUInt32();
            uint t0 = reader.ReadUInt32(), t1 = reader.ReadUInt32(), t2 = reader.ReadUInt32();
            uint materialId = reader.ReadUInt32();
            uint smoothingGroup = reader.ReadUInt32();

            sb.Append(innerPad)
                .Append("v=(").Append(v0).Append(',').Append(v1).Append(',').Append(v2).Append(')')
                .Append(" n=(").Append(n0).Append(',').Append(n1).Append(',').Append(n2).Append(')')
                .Append(" t=(").Append(t0).Append(',').Append(t1).Append(',').Append(t2).Append(')')
                .Append(" material=").Append(materialId.ToString(CultureInfo.InvariantCulture))
                .Append(" smoothingGroup=0x").Append(smoothingGroup.ToString("X8", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        sb.Append(pad).Append("]\n");

        byte hasPhysics = reader.ReadByte();
        sb.Append(pad).Append("hasPhysics=").Append(hasPhysics != 0 ? "true" : "false").Append('\n');

        AppendLeftoverNote(reader, payloadStart, dataSize, sb, pad);
    }

    private static void DumpMaterial(BinaryReader reader, int dataSize, StringBuilder sb, string pad)
    {
        long payloadStart = reader.BaseStream.Position;

        string name = ReadString16(reader);
        string texture = ReadString16(reader);
        string alpha = ReadString16(reader);
        string environment = ReadString16(reader);

        uint shading = reader.ReadUInt32();
        var ambient = ReadVector3(reader);
        var diffuse = ReadVector3(reader);
        var specular = ReadVector3(reader);

        float shininess = reader.ReadSingle();
        float opacity = reader.ReadSingle();

        byte doubleSided = reader.ReadByte();
        uint textureTiling = reader.ReadUInt32();
        uint alphaTiling = reader.ReadUInt32();
        uint environmentTiling = reader.ReadUInt32();

        byte isColorKey = reader.ReadByte();
        uint colorKey = reader.ReadUInt32();

        uint attributeCount = reader.ReadUInt32();

        sb.Append(pad).Append("Material: \"").Append(name)
            .Append("\" -> texture \"").Append(texture).Append("\"\n");
        sb.Append(pad).Append("  alpha=\"").Append(alpha)
            .Append("\" environment=\"").Append(environment).Append("\"\n");
        sb.Append(pad).Append("  shading=").Append(shading.ToString(CultureInfo.InvariantCulture))
            .Append(" ambient=").Append(FormatVec3(ambient))
            .Append(" diffuse=").Append(FormatVec3(diffuse))
            .Append(" specular=").Append(FormatVec3(specular)).Append('\n');
        sb.Append(pad).Append("  shininess=").Append(FormatFloat(shininess))
            .Append(" opacity=").Append(FormatFloat(opacity))
            .Append(" doubleSided=").Append(doubleSided != 0 ? "true" : "false").Append('\n');
        sb.Append(pad).Append("  tiling=(texture=").Append(textureTiling)
            .Append(", alpha=").Append(alphaTiling)
            .Append(", environment=").Append(environmentTiling).Append(")\n");
        sb.Append(pad).Append("  isColorKey=").Append(isColorKey != 0 ? "true" : "false")
            .Append(" colorKey=0x").Append(colorKey.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("  attributeCount=").Append(attributeCount.ToString(CultureInfo.InvariantCulture)).Append('\n');

        AppendLeftoverNote(reader, payloadStart, dataSize, sb, pad);
    }

    private static void AppendLeftoverNote(BinaryReader reader, long payloadStart, int dataSize, StringBuilder sb, string pad)
    {
        long consumed = reader.BaseStream.Position - payloadStart;
        long remaining = dataSize - consumed;
        if (remaining > 0)
        {
            sb.Append(pad).Append('[').Append(remaining.ToString(CultureInfo.InvariantCulture))
                .Append(" bytes of further unknown data]\n");
        }
    }

    private static string ReadString16(BinaryReader reader)
    {
        ushort length = reader.ReadUInt16();
        return length == 0 ? string.Empty : Encoding.ASCII.GetString(reader.ReadBytes(length));
    }

    private static (float X, float Y, float Z) ReadVector3(BinaryReader reader) =>
        (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static (float X, float Y, float Z, float W) ReadQuaternion(BinaryReader reader) =>
        (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static string FormatFloat(float value) => value.ToString("G7", CultureInfo.InvariantCulture);

    private static string FormatVec3((float X, float Y, float Z) v) =>
        $"({FormatFloat(v.X)}, {FormatFloat(v.Y)}, {FormatFloat(v.Z)})";

    private static string FormatQuat((float X, float Y, float Z, float W) q) =>
        $"({FormatFloat(q.X)}, {FormatFloat(q.Y)}, {FormatFloat(q.Z)}, {FormatFloat(q.W)})";
}
