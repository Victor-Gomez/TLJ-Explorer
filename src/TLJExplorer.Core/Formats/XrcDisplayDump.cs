using System.Globalization;
using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Detailed text dumper for XRC "display" records.
/// </summary>
/// <remarks>
/// <para>
/// XRC data is a recursive tree of <c>TXRCRecord</c> nodes:
/// </para>
/// <code>
/// Byte   TypeId
/// Byte   SubType         -- record variant selector (see per-TypeId notes below)
/// UInt16 Tag2
/// UInt16 NameLen
/// char[NameLen] Name     -- ASCII, exact character count, no null terminator
/// Int32  DataSize
/// byte[DataSize] Data    -- this record's own payload, interpreted per (TypeId, SubType)
/// UInt16 NumChildren
/// UInt16 Unknown3        -- expected 0; a non-zero value indicates a structural anomaly
/// </code>
/// <para>
/// If <c>NumChildren &gt; 0</c>, that many complete <c>TXRCRecord</c>s follow immediately afterwards in
/// the stream (each of which may itself have children, recursively).
/// </para>
/// <para>
/// Field layouts are transcribed from ScummVM's Stark engine (<c>engines/stark/resources/*.cpp</c>
/// <c>readData</c>) which is the canonical decoder for these records. Notable primitive conventions:
/// <c>bool</c> is stored as a full <c>uint32</c> (nonzero means true), <c>Point</c> is two
/// <c>uint32</c>s, <c>Rect</c> is four <c>sint32</c>s (l,t,r,b), <c>Vector3</c> is three little-endian
/// floats, and <c>String</c> is a <c>uint16</c> length followed by that many Latin-1 bytes.
/// </para>
/// <para>
/// This dumper is independent from (and decodes far more record types/fields than) the lighter
/// structural XRC reader used elsewhere to build the virtual file tree; the two must not be confused.
/// </para>
/// </remarks>
public static class XrcDisplayDump
{
    // Type constants match ScummVM's Object::Type::ResourceType enum values (see resources/object.h).
    private const byte TypeLayer = 0x04;
    private const byte TypeCamera = 0x05;
    private const byte TypeFloor = 0x06;
    private const byte TypeFloorFace = 0x07;
    private const byte TypeItem = 0x08;
    private const byte TypeScript = 0x09;
    private const byte TypeAnimHierarchy = 0x0A;
    private const byte TypeAnim = 0x0B;
    private const byte TypeDirection = 0x0C;
    private const byte TypeImage = 0x0D;
    private const byte TypeAnimScriptItem = 0x0F;
    private const byte TypeSound = 0x10;
    private const byte TypePath = 0x11;
    private const byte TypeFloorField = 0x12;
    private const byte TypeBookmark = 0x13;
    private const byte TypeKnowledge = 0x15;
    private const byte TypeCommand = 0x16;
    private const byte TypePATTable = 0x17;
    private const byte TypeDialog = 0x1B;
    private const byte TypeSpeech = 0x1D;
    private const byte TypeLight = 0x1E;
    private const byte TypeBonesMesh = 0x20;
    private const byte TypeScroll = 0x21;
    private const byte TypeFMV = 0x22;
    private const byte TypeLipsync = 0x23;
    private const byte TypeAnimSoundTrigger = 0x24;
    private const byte TypeTextureSet = 0x26;

    /// <summary>
    /// Reads the full record tree from <paramref name="stream"/> (from the current position to EOF, one
    /// or more sibling root records) and renders it as an indented, brace-delimited text tree.
    /// </summary>
    public static string DumpAsText(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var sb = new StringBuilder();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            DumpRecord(reader, sb, indent: 0);
        }

        return sb.ToString();
    }

    private static void DumpRecord(BinaryReader reader, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);

        byte typeId = reader.ReadByte();
        byte subType = reader.ReadByte();
        _ = reader.ReadUInt16(); // Tag2
        ushort nameLen = reader.ReadUInt16();
        string name = nameLen > 0 ? Encoding.Latin1.GetString(reader.ReadBytes(nameLen)) : string.Empty;
        int dataSize = reader.ReadInt32();
        byte[] data = dataSize > 0 ? reader.ReadBytes(dataSize) : Array.Empty<byte>();
        ushort numChildren = reader.ReadUInt16();
        // Trailing "unknown3" field expected to be 0; a non-zero value indicates a structural anomaly.
        ushort unknown3 = reader.ReadUInt16();

        sb.Append(pad).Append(name).Append(" { <").Append(FormatTypeLabel(typeId, subType)).Append(">\n");

        if (unknown3 != 0)
        {
            sb.Append(pad).Append("  [warning: unknown3 = 0x")
                .Append(unknown3.ToString("X4", CultureInfo.InvariantCulture))
                .Append(" (expected 0)]\n");
        }

        int consumed = DecodePayload(typeId, subType, data, sb, indent + 1);
        int remaining = data.Length - consumed;
        if (remaining > 0)
        {
            sb.Append(pad).Append("  [")
                .Append(remaining.ToString(CultureInfo.InvariantCulture))
                .Append(" bytes undecoded (typeId=0x")
                .Append(typeId.ToString("X2", CultureInfo.InvariantCulture))
                .Append(", subType=")
                .Append(subType.ToString(CultureInfo.InvariantCulture))
                .Append(")]\n");
        }

        for (int i = 0; i < numChildren; i++)
        {
            DumpRecord(reader, sb, indent + 1);
        }

        sb.Append(pad).Append("}\n");
    }

    /// <summary>
    /// Decodes a record's <c>Data</c> payload according to its <c>TypeId</c> (and <c>SubType</c> where
    /// the variant matters), appending human-readable lines to <paramref name="sb"/>. Returns the
    /// number of bytes consumed from the start of <paramref name="data"/> so the caller can report any
    /// undecoded trailing bytes.
    /// </summary>
    private static int DecodePayload(byte typeId, byte subType, byte[] data, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);

        switch (typeId)
        {
            case TypeLayer:
                return DecodeLayer(subType, data, sb, pad);
            case TypeCamera:
                return DecodeCamera(data, sb, pad);
            case TypeFloor:
                return DecodeFloor(data, sb, pad);
            case TypeFloorFace:
                return DecodeFloorFace(data, sb, pad);
            case TypeItem:
                return DecodeItem(subType, data, sb, pad);
            case TypeScript:
                return DecodeScript(data, sb, pad);
            case TypeAnimHierarchy:
                return DecodeAnimHierarchy(data, sb, pad);
            case TypeAnim:
                return DecodeAnim(subType, data, sb, pad);
            case TypeDirection:
                return DecodeDirection(data, sb, pad);
            case TypeImage:
                return DecodeImage(subType, data, sb, pad);
            case TypeAnimScriptItem:
                return DecodeAnimScriptItem(data, sb, pad);
            case TypeSound:
                return DecodeSound(data, sb, pad);
            case TypePath:
                return DecodePath(subType, data, sb, pad);
            case TypeFloorField:
                return DecodeFloorField(data, sb, pad);
            case TypeBookmark:
                return DecodeBookmark(data, sb, pad);
            case TypeKnowledge:
                return DecodeKnowledge(subType, data, sb, pad);
            case TypeCommand:
                return DecodeCommand(data, sb, pad);
            case TypePATTable:
                return DecodePATTable(data, sb, pad);
            case TypeDialog:
                return DecodeDialog(data, sb, pad);
            case TypeSpeech:
                return DecodeSpeech(data, sb, pad);
            case TypeLight:
                return DecodeLight(data, sb, pad);
            case TypeBonesMesh:
                return DecodeSingleFilename("Mesh file", data, sb, pad);
            case TypeScroll:
                return DecodeScroll(data, sb, pad);
            case TypeFMV:
                return DecodeFMV(data, sb, pad);
            case TypeLipsync:
                return DecodeLipsync(data, sb, pad);
            case TypeAnimSoundTrigger:
                return DecodeAnimSoundTrigger(data, sb, pad);
            case TypeTextureSet:
                return DecodeSingleFilename("Texture file", data, sb, pad);
            default:
                return 0;
        }
    }

    // ---------------------------------------------------------------------
    // Per-type decoders. Every decoder returns the number of payload bytes
    // consumed; the outer DumpRecord uses that to report any trailing bytes.
    // ---------------------------------------------------------------------

    private static int DecodeLayer(byte subType, byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadFloat(data, ref offset, out float scrollScale)) return offset;
        sb.Append(pad).Append("ScrollScale: ").Append(FormatFloat(scrollScale)).Append('\n');

        switch (subType)
        {
            case 1: // Layer2D
            {
                if (!TryReadUInt32(data, ref offset, out uint itemsCount)) return offset;
                sb.Append(pad).Append("Items: ").Append(itemsCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
                for (uint i = 0; i < itemsCount; i++)
                {
                    if (!TryReadUInt32(data, ref offset, out uint itemIndex)) return offset;
                    sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append("] itemIndex=").Append(itemIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                if (!TryReadBool32(data, ref offset, out bool enabled)) return offset;
                sb.Append(pad).Append("Enabled: ").Append(enabled).Append('\n');
                break;
            }
            case 2: // Layer3D
            {
                if (!TryReadBool32(data, ref offset, out bool shouldRenderShadows)) return offset;
                sb.Append(pad).Append("ShouldRenderShadows: ").Append(shouldRenderShadows).Append('\n');
                if (!TryReadFloat(data, ref offset, out float nearClip)) return offset;
                sb.Append(pad).Append("NearClipPlane: ").Append(FormatFloat(nearClip)).Append('\n');
                if (!TryReadFloat(data, ref offset, out float farClip)) return offset;
                sb.Append(pad).Append("FarClipPlane: ").Append(FormatFloat(farClip)).Append('\n');
                if (offset < data.Length)
                {
                    if (!TryReadUInt32(data, ref offset, out uint maxShadowLength)) return offset;
                    sb.Append(pad).Append("MaxShadowLength: ").Append(maxShadowLength.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                break;
            }
        }

        return offset;
    }

    private static int DecodeCamera(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadVector3(data, ref offset, out var position)) return offset;
        if (!TryReadVector3(data, ref offset, out var lookDir)) return offset;
        if (!TryReadFloat(data, ref offset, out float f1)) return offset;
        if (!TryReadFloat(data, ref offset, out float fov)) return offset;
        if (!TryReadRect(data, ref offset, out var viewSize)) return offset;
        if (!TryReadVector3(data, ref offset, out var v4)) return offset;

        sb.Append(pad).Append("Position: ").Append(FormatVector3(position)).Append('\n');
        sb.Append(pad).Append("LookDirection: ").Append(FormatVector3(lookDir)).Append('\n');
        sb.Append(pad).Append("f1: ").Append(FormatFloat(f1)).Append('\n');
        sb.Append(pad).Append("FOV: ").Append(FormatFloat(fov)).Append('\n');
        sb.Append(pad).Append("ViewSize: ").Append(FormatRect(viewSize)).Append('\n');
        sb.Append(pad).Append("v4: ").Append(FormatVector3(v4)).Append('\n');
        return offset;
    }

    private static int DecodeFloor(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint facesCount)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint vertexCount)) return offset;
        sb.Append(pad).Append("FacesCount: ").Append(facesCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("VertexCount: ").Append(vertexCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (uint i = 0; i < vertexCount; i++)
        {
            if (!TryReadVector3(data, ref offset, out var v)) return offset;
            sb.Append(pad).Append("  v[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("]=")
                .Append(FormatVector3(v)).Append('\n');
        }
        return offset;
    }

    private static int DecodeFloorFace(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadInt16(data, ref offset, out short i0)) return offset;
        if (!TryReadInt16(data, ref offset, out short i1)) return offset;
        if (!TryReadInt16(data, ref offset, out short i2)) return offset;
        if (!TryReadFloat(data, ref offset, out float dist)) return offset;
        if (!TryReadInt16(data, ref offset, out short _)) return offset;
        if (!TryReadInt16(data, ref offset, out short _)) return offset;
        if (!TryReadInt16(data, ref offset, out short _)) return offset;
        if (!TryReadFloat(data, ref offset, out float unk2)) return offset;

        sb.Append(pad).Append("Indices: [").Append(i0).Append(", ").Append(i1).Append(", ").Append(i2).Append("]\n");
        sb.Append(pad).Append("DistanceFromCamera: ").Append(FormatFloat(dist)).Append('\n');
        sb.Append(pad).Append("Unk2: ").Append(FormatFloat(unk2)).Append('\n');
        return offset;
    }

    private static int DecodeItem(byte subType, byte[] data, StringBuilder sb, string pad)
    {
        // All Item subtypes share Item::readData at the front: enabled(bool32) + characterIndex(sint32).
        // Every visual subclass then adds ItemVisual::readData: clickable(bool32). Then per subtype.
        int offset = 0;
        if (!TryReadBool32(data, ref offset, out bool enabled)) return offset;
        if (!TryReadInt32(data, ref offset, out int characterIndex)) return offset;
        sb.Append(pad).Append("Enabled: ").Append(enabled).Append('\n');
        sb.Append(pad).Append("CharacterIndex: ").Append(characterIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("SubType: ").Append(ItemSubTypeName(subType)).Append(" (").Append(subType).Append(")\n");

        switch (subType)
        {
            case 1: // GlobalItemTemplate — ItemTemplate (no clickable)
                break;
            case 3: // LevelItemTemplate — ItemTemplate + reference
                if (!TryReadResourceReference(data, ref offset, out string levelRef)) return offset;
                sb.Append(pad).Append("Reference: ").Append(levelRef).Append('\n');
                break;
            case 2: // InventoryItem — ItemVisual only
                if (!TryReadBool32(data, ref offset, out bool clickable2)) return offset;
                sb.Append(pad).Append("Clickable: ").Append(clickable2).Append('\n');
                break;
            case 5: // FloorPositionedImageItem — static prop
            case 6: // FloorPositionedImageItem — animated prop
            {
                if (!TryReadBool32(data, ref offset, out bool clickable)) return offset;
                if (!TryReadInt32(data, ref offset, out int floorFaceIndex)) return offset;
                if (!TryReadPoint(data, ref offset, out int px, out int py)) return offset;
                sb.Append(pad).Append("Clickable: ").Append(clickable).Append('\n');
                sb.Append(pad).Append("FloorFaceIndex: ").Append(floorFaceIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(pad).Append("Position: (").Append(px).Append(", ").Append(py).Append(")\n");
                break;
            }
            case 7: // ImageItem — background element
            case 8: // ImageItem — background
            {
                if (!TryReadBool32(data, ref offset, out bool clickable)) return offset;
                if (!TryReadPoint(data, ref offset, out int px, out int py)) return offset;
                if (!TryReadResourceReference(data, ref offset, out string imgRef)) return offset;
                sb.Append(pad).Append("Clickable: ").Append(clickable).Append('\n');
                sb.Append(pad).Append("Position: (").Append(px).Append(", ").Append(py).Append(")\n");
                sb.Append(pad).Append("Reference: ").Append(imgRef).Append('\n');
                break;
            }
            case 10: // ModelItem — FloorPositionedItem + reference
            {
                if (!TryReadBool32(data, ref offset, out bool clickable)) return offset;
                if (!TryReadResourceReference(data, ref offset, out string modelRef)) return offset;
                sb.Append(pad).Append("Clickable: ").Append(clickable).Append('\n');
                sb.Append(pad).Append("Reference: ").Append(modelRef).Append('\n');
                break;
            }
        }

        return offset;
    }

    private static int DecodeScript(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint scriptType)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint runEvent)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint minChapter)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint maxChapter)) return offset;
        if (!TryReadBool32(data, ref offset, out bool resetGameSpeed)) return offset;

        // In ScummVM, `type == 0` -> enabled. Other values 1..2 select passive/onGameEvent semantics for
        // the kSubTypeGameEvent subtype; a raw dump reports the value and lets the reader interpret it.
        sb.Append(pad).Append("Type: ").Append(scriptType.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(ScriptTypeName(scriptType)).Append(")\n");
        sb.Append(pad).Append("RunEvent: ").Append(runEvent.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("MinChapter: ").Append(minChapter.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("MaxChapter: ").Append(maxChapter.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("ShouldResetGameSpeed: ").Append(resetGameSpeed).Append('\n');
        return offset;
    }

    private static int DecodeAnimHierarchy(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint refCount)) return offset;
        sb.Append(pad).Append("AnimationRefs: ").Append(refCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (uint i = 0; i < refCount; i++)
        {
            if (!TryReadResourceReference(data, ref offset, out string r)) return offset;
            sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(r).Append('\n');
        }
        if (!TryReadResourceReference(data, ref offset, out string parentRef)) return offset;
        sb.Append(pad).Append("ParentAnimHierarchy: ").Append(parentRef).Append('\n');
        if (!TryReadFloat(data, ref offset, out float field5C)) return offset;
        sb.Append(pad).Append("field_5C: ").Append(FormatFloat(field5C)).Append('\n');
        return offset;
    }

    private static int DecodeAnim(byte subType, byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint activity)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint numFrames)) return offset;
        sb.Append(pad).Append("Activity: ").Append(activity.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(ActivityName((int)activity)).Append(")\n");
        sb.Append(pad).Append("NumFrames: ").Append(numFrames.ToString(CultureInfo.InvariantCulture)).Append('\n');

        switch (subType)
        {
            case 1: // AnimImages
                if (!TryReadFloat(data, ref offset, out float field3C)) return offset;
                sb.Append(pad).Append("field_3C: ").Append(FormatFloat(field3C)).Append('\n');
                break;
            case 2: // AnimProp
            {
                if (!TryReadPascalString(data, ref offset, out string field3Cstr)) return offset;
                sb.Append(pad).Append("field_3C: \"").Append(field3Cstr).Append("\"\n");
                if (!TryReadUInt32(data, ref offset, out uint meshCount)) return offset;
                sb.Append(pad).Append("MeshCount: ").Append(meshCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
                for (uint i = 0; i < meshCount; i++)
                {
                    if (!TryReadPascalString(data, ref offset, out string mf)) return offset;
                    sb.Append(pad).Append("  mesh[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("]=\"").Append(mf).Append("\"\n");
                }
                if (!TryReadPascalString(data, ref offset, out string tex)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint moveSpeed)) return offset;
                sb.Append(pad).Append("Texture file: \"").Append(tex).Append("\"\n");
                sb.Append(pad).Append("MovementSpeed: ").Append(moveSpeed.ToString(CultureInfo.InvariantCulture)).Append('\n');
                break;
            }
            case 3: // AnimVideo
            {
                if (!TryReadPascalString(data, ref offset, out string smacker)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint width)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint height)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint frameCount)) return offset;
                sb.Append(pad).Append("Smacker file: \"").Append(smacker).Append("\"\n");
                sb.Append(pad).Append("Size: ").Append(width).Append('x').Append(height).Append('\n');
                sb.Append(pad).Append("Frames: ").Append(frameCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
                for (uint i = 0; i < frameCount; i++)
                {
                    if (!TryReadPoint(data, ref offset, out int px, out int py)) return offset;
                    if (!TryReadRect(data, ref offset, out int rl, out int rt, out int rr, out int rb)) return offset;
                    sb.Append(pad).Append("  frame[").Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append("] pos=(").Append(px).Append(", ").Append(py)
                        .Append(") rect=(").Append(rl).Append(", ").Append(rt).Append(", ").Append(rr).Append(", ").Append(rb).Append(")\n");
                }
                if (!TryReadBool32(data, ref offset, out bool loop)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint fpsOverride)) return offset;
                sb.Append(pad).Append("Loop: ").Append(loop).Append('\n');
                sb.Append(pad).Append("FrameRateOverride: ").Append(fpsOverride.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (offset < data.Length)
                {
                    if (!TryReadBool32(data, ref offset, out bool preload)) return offset;
                    sb.Append(pad).Append("Preload: ").Append(preload).Append('\n');
                }
                break;
            }
            case 4: // AnimSkeleton
            {
                if (!TryReadPascalString(data, ref offset, out string animFile)) return offset;
                sb.Append(pad).Append("Animation file: \"").Append(animFile).Append("\"\n");
                // Three trailing strings ScummVM discards; still consume them so we don't misalign.
                for (int i = 0; i < 3; i++)
                {
                    if (!TryReadPascalString(data, ref offset, out _)) return offset;
                }
                if (!TryReadBool32(data, ref offset, out bool loop)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint moveSpeed)) return offset;
                sb.Append(pad).Append("Loop: ").Append(loop).Append('\n');
                sb.Append(pad).Append("MovementSpeed: ").Append(moveSpeed.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (offset < data.Length)
                {
                    if (!TryReadBool32(data, ref offset, out bool castsShadow)) return offset;
                    sb.Append(pad).Append("CastsShadow: ").Append(castsShadow).Append('\n');
                }
                if (offset < data.Length)
                {
                    if (!TryReadUInt32(data, ref offset, out uint idleFreq)) return offset;
                    sb.Append(pad).Append("IdleActionFrequency: ").Append(idleFreq.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                break;
            }
        }

        return offset;
    }

    private static int DecodeDirection(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint a)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint b)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint c)) return offset;
        sb.Append(pad).Append("field_34: ").Append(a.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("field_38: ").Append(b.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("field_3C: ").Append(c.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return offset;
    }

    private static int DecodeImage(byte subType, byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadPascalString(data, ref offset, out string filename)) return offset;
        if (!TryReadPoint(data, ref offset, out int hx, out int hy)) return offset;
        if (!TryReadBool32(data, ref offset, out bool transparent)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint transparentColor)) return offset;
        sb.Append(pad).Append("Image file: \"").Append(filename).Append("\"\n");
        sb.Append(pad).Append("Hotspot: (").Append(hx).Append(", ").Append(hy).Append(")\n");
        sb.Append(pad).Append("Transparent: ").Append(transparent).Append('\n');
        sb.Append(pad).Append("TransparentColor: 0x")
            .Append(transparentColor.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');

        if (!TryReadUInt32(data, ref offset, out uint polyCount)) return offset;
        sb.Append(pad).Append("HitPolygons: ").Append(polyCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (uint i = 0; i < polyCount; i++)
        {
            if (!TryReadUInt32(data, ref offset, out uint pointCount)) return offset;
            sb.Append(pad).Append("  poly[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] points=")
                .Append(pointCount.ToString(CultureInfo.InvariantCulture)).Append(':');
            for (uint j = 0; j < pointCount; j++)
            {
                if (!TryReadPoint(data, ref offset, out int px, out int py)) return offset;
                sb.Append(" (").Append(px).Append(", ").Append(py).Append(')');
            }
            sb.Append('\n');
        }

        if (subType == 2 || subType == 3) // ImageStill
        {
            if (offset < data.Length)
            {
                if (!TryReadUInt32(data, ref offset, out uint f44)) return offset;
                sb.Append(pad).Append("field_44 (÷33): ").Append((f44 / 33).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (offset < data.Length)
            {
                if (!TryReadUInt32(data, ref offset, out uint f48)) return offset;
                sb.Append(pad).Append("field_48: ").Append(f48.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        else if (subType == 4) // ImageText
        {
            if (!TryReadPoint(data, ref offset, out int sx, out int sy)) return offset;
            if (!TryReadPascalString(data, ref offset, out string text)) return offset;
            if (offset + 4 > data.Length) return offset;
            byte r = data[offset++], g = data[offset++], b = data[offset++], _ = data[offset++];
            if (!TryReadUInt32(data, ref offset, out uint font)) return offset;
            sb.Append(pad).Append("Size: ").Append(sx).Append('x').Append(sy).Append('\n');
            sb.Append(pad).Append("Text: \"").Append(text).Append("\"\n");
            sb.Append(pad).Append("Color: #")
                .Append(r.ToString("X2", CultureInfo.InvariantCulture))
                .Append(g.ToString("X2", CultureInfo.InvariantCulture))
                .Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(pad).Append("Font: ").Append(font.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return offset;
    }

    private static int DecodeAnimScriptItem(byte[] data, StringBuilder sb, string pad)
    {
        // Every opcode reads exactly three uint32s: opcode, duration, operand. Opcodes 3 and 5 carry
        // additional sub-fields packed inside `operand` (min/max frame pair, stock-sound id, etc.); we
        // surface those where documented in ScummVM's animscript.cpp.
        int offset = 0;
        if (!TryReadInt32(data, ref offset, out int opcode)) return offset;
        if (!TryReadInt32(data, ref offset, out int duration)) return offset;

        switch (opcode)
        {
            case 0: // DisplayFrame
            {
                if (!TryReadInt32(data, ref offset, out int frame)) return offset;
                sb.Append(pad).Append("DisplayFrame(duration=").Append(duration)
                    .Append(", frame=").Append(frame).Append(")\n");
                break;
            }
            case 1: // PlayAnimSound
            {
                if (!TryReadInt32(data, ref offset, out int operand)) return offset;
                sb.Append(pad).Append("PlayAnimSound(duration=").Append(duration)
                    .Append(", operand=").Append(operand).Append(")\n");
                break;
            }
            case 2: // GoToItem
            {
                if (!TryReadInt32(data, ref offset, out int line)) return offset;
                sb.Append(pad).Append("GoToItem(duration=").Append(duration)
                    .Append(", line=").Append(line).Append(")\n");
                break;
            }
            case 3: // DisplayRandomFrame — packs (maxFrame:int16, minFrame:int16) into the operand slot
            {
                if (!TryReadInt16(data, ref offset, out short maxFrame)) return offset;
                if (!TryReadInt16(data, ref offset, out short minFrame)) return offset;
                sb.Append(pad).Append("DisplayRandomFrame(duration=").Append(duration)
                    .Append(", min=").Append(minFrame).Append(", max=").Append(maxFrame).Append(")\n");
                break;
            }
            case 4: // SleepRandomDuration
            {
                if (!TryReadInt32(data, ref offset, out int maxWait)) return offset;
                sb.Append(pad).Append("SleepRandomDuration(duration=").Append(duration)
                    .Append(", maxWait=").Append(maxWait).Append(")\n");
                break;
            }
            case 5: // PlayStockSound
            {
                if (!TryReadInt32(data, ref offset, out int stockId)) return offset;
                sb.Append(pad).Append("PlayStockSound(duration=").Append(duration)
                    .Append(", stockId=").Append(stockId).Append(")\n");
                break;
            }
            default:
            {
                if (!TryReadUInt32(data, ref offset, out uint operand)) return offset;
                sb.Append(pad).Append(AnimScriptOpcodeName(opcode)).Append("(opcode=").Append(opcode)
                    .Append(", duration=").Append(duration)
                    .Append(", operand=").Append(operand).Append(")\n");
                break;
            }
        }

        return offset;
    }

    private static int DecodeSound(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadPascalString(data, ref offset, out string filename)) return offset;
        sb.Append(pad).Append("Sound file: \"").Append(filename).Append("\"\n");
        if (!TryReadUInt32(data, ref offset, out uint enabled)) return offset;
        if (!TryReadBool32(data, ref offset, out bool looping)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint field64)) return offset;
        if (!TryReadBool32(data, ref offset, out bool loopIndefinitely)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint maxDuration)) return offset;
        if (!TryReadBool32(data, ref offset, out bool loadFromFile)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint stockSoundType)) return offset;
        if (!TryReadPascalString(data, ref offset, out string soundName)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint field6C)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint soundType)) return offset;
        if (!TryReadFloat(data, ref offset, out float pan)) return offset;
        if (!TryReadFloat(data, ref offset, out float volume)) return offset;

        sb.Append(pad).Append("Enabled: ").Append(enabled.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("Looping: ").Append(looping).Append('\n');
        sb.Append(pad).Append("field_64: ").Append(field64.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("LoopIndefinitely: ").Append(loopIndefinitely).Append('\n');
        sb.Append(pad).Append("MaxDuration: ").Append(maxDuration.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("LoadFromFile: ").Append(loadFromFile).Append('\n');
        sb.Append(pad).Append("StockSoundType: ").Append(stockSoundType.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(SoundStockTypeName(stockSoundType)).Append(")\n");
        sb.Append(pad).Append("SoundName: \"").Append(soundName).Append("\"\n");
        sb.Append(pad).Append("field_6C: ").Append(field6C.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("SoundType: ").Append(soundType.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(SoundTypeName(soundType)).Append(")\n");
        sb.Append(pad).Append("Pan: ").Append(FormatFloat(pan)).Append('\n');
        sb.Append(pad).Append("Volume: ").Append(FormatFloat(volume)).Append('\n');
        return offset;
    }

    private static int DecodePath(byte subType, byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint field30)) return offset;
        sb.Append(pad).Append("field_30: ").Append(field30.ToString(CultureInfo.InvariantCulture)).Append('\n');

        if (subType == 1) // Path2D
        {
            if (!TryReadUInt32(data, ref offset, out uint vertexCount)) return offset;
            sb.Append(pad).Append("Vertices: ").Append(vertexCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (uint i = 0; i < vertexCount; i++)
            {
                if (!TryReadFloat(data, ref offset, out float weight)) return offset;
                if (!TryReadPoint(data, ref offset, out int px, out int py)) return offset;
                sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append("] weight=").Append(FormatFloat(weight))
                    .Append(" pos=(").Append(px).Append(", ").Append(py).Append(")\n");
            }
            if (!TryReadUInt32(data, ref offset, out uint _)) return offset;
        }
        else if (subType == 2) // Path3D
        {
            if (!TryReadUInt32(data, ref offset, out uint vertexCount)) return offset;
            sb.Append(pad).Append("Vertices: ").Append(vertexCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (uint i = 0; i < vertexCount; i++)
            {
                if (!TryReadFloat(data, ref offset, out float weight)) return offset;
                if (!TryReadVector3(data, ref offset, out var pos)) return offset;
                sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append("] weight=").Append(FormatFloat(weight))
                    .Append(" pos=").Append(FormatVector3(pos)).Append('\n');
            }
            if (!TryReadFloat(data, ref offset, out float sortKey)) return offset;
            sb.Append(pad).Append("SortKey: ").Append(FormatFloat(sortKey)).Append('\n');
        }

        return offset;
    }

    private static int DecodeFloorField(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint count)) return offset;
        sb.Append(pad).Append("FacesInFloorField: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        int available = Math.Min((int)count, data.Length - offset);
        if (available > 0)
        {
            sb.Append(pad).Append("  bytes:");
            for (int i = 0; i < available; i++)
                sb.Append(' ').Append(data[offset + i].ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        offset += available;
        return offset;
    }

    private static int DecodeBookmark(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadFloat(data, ref offset, out float x)) return offset;
        if (!TryReadFloat(data, ref offset, out float y)) return offset;
        sb.Append(pad).Append("Position: (").Append(FormatFloat(x)).Append(", ").Append(FormatFloat(y)).Append(")\n");
        return offset;
    }

    private static int DecodeKnowledge(byte subType, byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        switch (subType)
        {
            case 0: // kBoolean
            case 5: // kBooleanWithChild
                if (!TryReadBool32(data, ref offset, out bool b)) return offset;
                sb.Append(pad).Append("Value (bool): ").Append(b).Append('\n');
                break;
            case 2: // kInteger
            case 3: // kInteger2
                if (!TryReadInt32(data, ref offset, out int iv)) return offset;
                sb.Append(pad).Append("Value (int): ").Append(iv.ToString(CultureInfo.InvariantCulture)).Append('\n');
                break;
            case 4: // kReference
                if (!TryReadResourceReference(data, ref offset, out string r)) return offset;
                sb.Append(pad).Append("Value (ref): ").Append(r).Append('\n');
                break;
            default:
                sb.Append(pad).Append("[unknown knowledge subtype ").Append(subType).Append("]\n");
                break;
        }
        return offset;
    }

    private static int DecodeCommand(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint count)) return offset;
        sb.Append(pad).Append("Arguments: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (uint i = 0; i < count; i++)
        {
            if (!TryReadUInt32(data, ref offset, out uint argType)) return offset;
            switch (argType)
            {
                case 0:
                    sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] int1 = 0 (shortcut)\n");
                    break;
                case 1: // kTypeInteger1
                case 2: // kTypeInteger2
                    if (!TryReadInt32(data, ref offset, out int iv)) return offset;
                    sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append("] int").Append(argType).Append(" = ").Append(iv.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                case 3: // kTypeResourceReference
                    if (!TryReadResourceReference(data, ref offset, out string r)) return offset;
                    sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ref = ").Append(r).Append('\n');
                    break;
                case 4: // kTypeString
                    if (!TryReadPascalString(data, ref offset, out string s)) return offset;
                    sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] str = \"").Append(s).Append("\"\n");
                    break;
                default:
                    sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append("] <unknown argType ").Append(argType.ToString(CultureInfo.InvariantCulture)).Append(">\n");
                    return offset;
            }
        }
        return offset;
    }

    private static int DecodePATTable(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint entryCount)) return offset;
        sb.Append(pad).Append("Entries: ").Append(entryCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (uint i = 0; i < entryCount; i++)
        {
            if (!TryReadInt32(data, ref offset, out int actionType)) return offset;
            if (!TryReadInt32(data, ref offset, out int scriptIndex)) return offset;
            sb.Append(pad).Append("  [").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("] actionType=").Append(actionType)
                .Append(" scriptIndex=").Append(scriptIndex).Append('\n');
        }
        if (!TryReadInt32(data, ref offset, out int defaultAction)) return offset;
        sb.Append(pad).Append("DefaultAction: ").Append(defaultAction).Append('\n');
        return offset;
    }

    private static int DecodeDialog(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint hasAskAbout)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint character)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint numTopics)) return offset;
        sb.Append(pad).Append("HasAskAbout: ").Append(hasAskAbout.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("Character: ").Append(character.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("Topics: ").Append(numTopics.ToString(CultureInfo.InvariantCulture)).Append('\n');

        for (uint t = 0; t < numTopics; t++)
        {
            if (!TryReadBool32(data, ref offset, out bool removeOnceDepleted)) return offset;
            if (!TryReadUInt32(data, ref offset, out uint numReplies)) return offset;
            sb.Append(pad).Append("  topic[").Append(t.ToString(CultureInfo.InvariantCulture))
                .Append("] removeOnceDepleted=").Append(removeOnceDepleted)
                .Append(" replies=").Append(numReplies.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (uint r = 0; r < numReplies; r++)
            {
                if (!TryReadUInt32(data, ref offset, out uint conditionType)) return offset;
                if (!TryReadResourceReference(data, ref offset, out string condRef)) return offset;
                if (!TryReadResourceReference(data, ref offset, out string condScriptRef)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint conditionReversed)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint field88)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint minChapter)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint maxChapter)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint noCaption)) return offset;
                if (!TryReadInt32(data, ref offset, out int nextDialogIndex)) return offset;
                if (!TryReadResourceReference(data, ref offset, out string nextScriptRef)) return offset;
                if (!TryReadUInt32(data, ref offset, out uint numLines)) return offset;
                sb.Append(pad).Append("    reply[").Append(r.ToString(CultureInfo.InvariantCulture))
                    .Append("] conditionType=").Append(conditionType.ToString(CultureInfo.InvariantCulture))
                    .Append(" reversed=").Append(conditionReversed.ToString(CultureInfo.InvariantCulture))
                    .Append(" chapters=[").Append(minChapter.ToString(CultureInfo.InvariantCulture))
                    .Append("..").Append(maxChapter.ToString(CultureInfo.InvariantCulture))
                    .Append("] noCaption=").Append(noCaption.ToString(CultureInfo.InvariantCulture))
                    .Append(" nextDialog=").Append(nextDialogIndex)
                    .Append(" field_88=").Append(field88.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
                sb.Append(pad).Append("      condRef=").Append(condRef).Append('\n');
                sb.Append(pad).Append("      condScriptRef=").Append(condScriptRef).Append('\n');
                sb.Append(pad).Append("      nextScriptRef=").Append(nextScriptRef).Append('\n');
                sb.Append(pad).Append("      lines: ").Append(numLines.ToString(CultureInfo.InvariantCulture)).Append('\n');
                for (uint l = 0; l < numLines; l++)
                {
                    if (!TryReadResourceReference(data, ref offset, out string a)) return offset;
                    if (!TryReadResourceReference(data, ref offset, out string b)) return offset;
                    sb.Append(pad).Append("        [").Append(l.ToString(CultureInfo.InvariantCulture))
                        .Append("] a=").Append(a).Append(" b=").Append(b).Append('\n');
                }
            }
        }
        return offset;
    }

    private static int DecodeSpeech(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadPascalString(data, ref offset, out string phrase)) return offset;
        if (!TryReadInt32(data, ref offset, out int character)) return offset;
        sb.Append(pad).Append("Subtitle: \"").Append(phrase).Append("\"\n");
        sb.Append(pad).Append("Character: ").Append(character.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return offset;
    }

    private static int DecodeLight(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadVector3(data, ref offset, out var color)) return offset;
        if (!TryReadVector3(data, ref offset, out var pos)) return offset;
        if (!TryReadVector3(data, ref offset, out var dir)) return offset;
        if (!TryReadFloat(data, ref offset, out float outerCone)) return offset;
        if (!TryReadFloat(data, ref offset, out float innerCone)) return offset;
        sb.Append(pad).Append("Color: ").Append(FormatVector3(color)).Append('\n');
        sb.Append(pad).Append("Position: ").Append(FormatVector3(pos)).Append('\n');
        sb.Append(pad).Append("Direction: ").Append(FormatVector3(dir)).Append('\n');
        sb.Append(pad).Append("OuterConeAngle: ").Append(FormatFloat(outerCone)).Append('\n');
        sb.Append(pad).Append("InnerConeAngle: ").Append(FormatFloat(innerCone)).Append('\n');
        if (offset < data.Length)
        {
            if (!TryReadFloat(data, ref offset, out float falloffNear)) return offset;
            if (!TryReadFloat(data, ref offset, out float falloffFar)) return offset;
            sb.Append(pad).Append("FalloffNear: ").Append(FormatFloat(falloffNear)).Append('\n');
            sb.Append(pad).Append("FalloffFar: ").Append(FormatFloat(falloffFar)).Append('\n');
        }
        return offset;
    }

    private static int DecodeScroll(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint coord)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint f30)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint f34)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint bookmarkIndex)) return offset;
        sb.Append(pad).Append("Coordinate: ").Append(coord.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("field_30: ").Append(f30.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("field_34: ").Append(f34.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("BookmarkIndex: ").Append(bookmarkIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return offset;
    }

    private static int DecodeFMV(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadPascalString(data, ref offset, out string filename)) return offset;
        if (!TryReadBool32(data, ref offset, out bool diaryAdd)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint gameDisc)) return offset;
        sb.Append(pad).Append("FMV file: \"").Append(filename).Append("\"\n");
        sb.Append(pad).Append("DiaryAddEntryOnPlay: ").Append(diaryAdd).Append('\n');
        sb.Append(pad).Append("GameDisc: ").Append(gameDisc.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return offset;
    }

    private static int DecodeLipsync(byte[] data, StringBuilder sb, string pad)
    {
        // Stride-8 encoding: each shape's ASCII byte lives 8 bytes apart within the leading array,
        // offset by 4 within its stride, starting right after the leading ShapeCount field.
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint shapeCount)) return offset;

        var syncChars = new char[Math.Max(0, (int)shapeCount)];
        for (int i = 0; i < shapeCount; i++)
        {
            int byteOffset = 4 + i * 8 + 4;
            syncChars[i] = byteOffset < data.Length ? (char)data[byteOffset] : '\0';
        }

        string sync = new(syncChars);
        sb.Append(pad).Append("Lipsync data: \"").Append(sync).Append("\"\n");

        offset = 4 + (int)shapeCount * 8;
        if (!TryReadUInt32(data, ref offset, out uint unkCount)) return offset;
        int skip = Math.Min((int)unkCount, Math.Max(0, data.Length - offset));
        offset += skip;
        if (unkCount > 0)
            sb.Append(pad).Append("UnkTail: ").Append(unkCount.ToString(CultureInfo.InvariantCulture)).Append(" bytes\n");
        return offset;
    }

    private static int DecodeAnimSoundTrigger(byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint triggerTime)) return offset;
        if (!TryReadUInt32(data, ref offset, out uint stockType)) return offset;
        sb.Append(pad).Append("SoundTriggerTime: ").Append(triggerTime.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(pad).Append("SoundStockType: ").Append(stockType.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return offset;
    }

    private static int DecodeSingleFilename(string label, byte[] data, StringBuilder sb, string pad)
    {
        int offset = 0;
        if (!TryReadPascalString(data, ref offset, out string filename)) return offset;
        sb.Append(pad).Append(label).Append(": \"").Append(filename).Append("\"\n");
        return offset;
    }

    // ---------------------------------------------------------------------
    // Primitive readers. Every one advances `offset` on success and returns
    // false (leaving `offset` at end-of-data) when the buffer is too short.
    // ---------------------------------------------------------------------

    private static bool TryReadUInt32(byte[] data, ref int offset, out uint value)
    {
        if (offset + 4 > data.Length) { offset = data.Length; value = 0; return false; }
        value = BitConverter.ToUInt32(data, offset);
        offset += 4;
        return true;
    }

    private static bool TryReadInt32(byte[] data, ref int offset, out int value)
    {
        if (offset + 4 > data.Length) { offset = data.Length; value = 0; return false; }
        value = BitConverter.ToInt32(data, offset);
        offset += 4;
        return true;
    }

    private static bool TryReadInt16(byte[] data, ref int offset, out short value)
    {
        if (offset + 2 > data.Length) { offset = data.Length; value = 0; return false; }
        value = BitConverter.ToInt16(data, offset);
        offset += 2;
        return true;
    }

    private static bool TryReadFloat(byte[] data, ref int offset, out float value)
    {
        if (offset + 4 > data.Length) { offset = data.Length; value = 0; return false; }
        value = BitConverter.ToSingle(data, offset);
        offset += 4;
        return true;
    }

    private static bool TryReadBool32(byte[] data, ref int offset, out bool value)
    {
        if (!TryReadUInt32(data, ref offset, out uint raw)) { value = false; return false; }
        value = raw != 0;
        return true;
    }

    private static bool TryReadPoint(byte[] data, ref int offset, out int x, out int y)
    {
        x = 0; y = 0;
        if (!TryReadInt32(data, ref offset, out x)) return false;
        if (!TryReadInt32(data, ref offset, out y)) return false;
        return true;
    }

    private static bool TryReadRect(byte[] data, ref int offset, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        if (!TryReadInt32(data, ref offset, out left)) return false;
        if (!TryReadInt32(data, ref offset, out top)) return false;
        if (!TryReadInt32(data, ref offset, out right)) return false;
        if (!TryReadInt32(data, ref offset, out bottom)) return false;
        return true;
    }

    private static bool TryReadRect(byte[] data, ref int offset, out (int L, int T, int R, int B) rect)
    {
        rect = default;
        if (!TryReadRect(data, ref offset, out int l, out int t, out int r, out int b)) return false;
        rect = (l, t, r, b);
        return true;
    }

    private static bool TryReadVector3(byte[] data, ref int offset, out (float X, float Y, float Z) v)
    {
        v = default;
        if (!TryReadFloat(data, ref offset, out float x)) return false;
        if (!TryReadFloat(data, ref offset, out float y)) return false;
        if (!TryReadFloat(data, ref offset, out float z)) return false;
        v = (x, y, z);
        return true;
    }

    private static bool TryReadPascalString(byte[] data, ref int offset, out string value)
    {
        // uint16 length prefix + Latin-1 bytes (see ScummVM XRCReadStream::readString).
        if (offset + 2 > data.Length) { offset = data.Length; value = string.Empty; return false; }
        ushort length = BitConverter.ToUInt16(data, offset);
        offset += 2;
        int available = Math.Min((int)length, Math.Max(0, data.Length - offset));
        value = available > 0 ? Encoding.Latin1.GetString(data, offset, available) : string.Empty;
        offset += available;
        return available == length;
    }

    private static bool TryReadResourceReference(byte[] data, ref int offset, out string formatted)
    {
        formatted = string.Empty;
        if (!TryReadUInt32(data, ref offset, out uint count)) return false;
        if (count > 32) // Sanity cap -- paths are only a handful of levels deep in practice.
        {
            offset = data.Length;
            return false;
        }

        var sb = new StringBuilder();
        sb.Append('[');
        for (uint i = 0; i < count; i++)
        {
            if (offset + 3 > data.Length) { offset = data.Length; return false; }
            byte type = data[offset++];
            ushort index = BitConverter.ToUInt16(data, offset);
            offset += 2;
            if (i > 0) sb.Append(" -> ");
            sb.Append('(').Append(TypeName(type)).Append(':').Append(index.ToString(CultureInfo.InvariantCulture)).Append(')');
        }
        sb.Append(']');
        formatted = sb.ToString();
        return true;
    }

    // ---------------------------------------------------------------------
    // Public helper preserved for BiffDump / other callers that already
    // rely on this signature.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads a length-prefixed string embedded in a record's <c>Data</c> buffer: a little-endian
    /// <see cref="ushort"/> character count at <paramref name="offset"/>, followed by that many ASCII
    /// characters. Returns the string and the number of bytes consumed (2 + length).
    /// </summary>
    public static (string Value, int BytesConsumed) ReadDataString(byte[] data, int offset)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (offset < 0 || offset + 2 > data.Length)
            return (string.Empty, 0);

        ushort length = BitConverter.ToUInt16(data, offset);
        int available = Math.Min((int)length, Math.Max(0, data.Length - offset - 2));
        string value = available > 0 ? Encoding.Latin1.GetString(data, offset + 2, available) : string.Empty;
        return (value, 2 + available);
    }

    // ---------------------------------------------------------------------
    // Formatting / naming helpers.
    // ---------------------------------------------------------------------

    private static string FormatFloat(float value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatVector3((float X, float Y, float Z) v) =>
        $"({FormatFloat(v.X)}, {FormatFloat(v.Y)}, {FormatFloat(v.Z)})";

    private static string FormatRect((int L, int T, int R, int B) r) =>
        $"(l={r.L}, t={r.T}, r={r.R}, b={r.B})";

    // -- named-enum lookups; values transcribed from ScummVM Stark resource headers --

    private static string ActivityName(int activity) => activity switch
    {
        0 => "unspecified",
        1 => "idle",
        2 => "walk",
        3 => "talk",
        6 => "run",
        10 => "idle-action",
        _ => $"unknown {activity.ToString(CultureInfo.InvariantCulture)}",
    };

    private static string ItemSubTypeName(byte subType) => subType switch
    {
        1 => "GlobalItemTemplate",
        2 => "InventoryItem",
        3 => "LevelItemTemplate",
        5 => "StaticProp",
        6 => "AnimatedProp",
        7 => "BackgroundElement",
        8 => "Background",
        10 => "Model",
        _ => "unknown",
    };

    private static string AnimSubTypeName(byte subType) => subType switch
    {
        1 => "Images",
        2 => "Prop",
        3 => "Video",
        4 => "Skeleton",
        _ => "unknown",
    };

    private static string ImageSubTypeName(byte subType) => subType switch
    {
        2 or 3 => "Still",
        4 => "Text",
        _ => "unknown",
    };

    private static string LayerSubTypeName(byte subType) => subType switch
    {
        1 => "2D",
        2 => "3D",
        _ => "unknown",
    };

    private static string PathSubTypeName(byte subType) => subType switch
    {
        1 => "2D",
        2 => "3D",
        _ => "unknown",
    };

    private static string KnowledgeSubTypeName(byte subType) => subType switch
    {
        0 => "Boolean",
        2 => "Integer",
        3 => "Integer2",
        4 => "Reference",
        5 => "BooleanWithChild",
        _ => "unknown",
    };

    private static string ContainerSubTypeName(byte subType) => subType switch
    {
        5 => "Sounds",
        8 => "StockSounds",
        _ => "unknown",
    };

    private static string ScriptSubTypeName(byte subType) => subType switch
    {
        4 => "GameEvent",
        5 => "PlayerAction",
        6 => "Dialog",
        _ => "unknown",
    };

    private static string ScriptTypeName(uint value) => value switch
    {
        0 => "OnGameEvent (or enabled)",
        1 => "PassiveDialog",
        2 => "OnPlayerAction",
        3 => "Type3",
        4 => "Type4",
        _ => "unknown",
    };

    private static string SoundTypeName(uint value) => value switch
    {
        0 => "Voice",
        1 => "Effect",
        2 => "Music",
        _ => "unknown",
    };

    private static string SoundStockTypeName(uint value) => value switch
    {
        3 => "Background",
        5 => "Stock",
        _ => "unknown",
    };

    private static string AnimScriptOpcodeName(int opcode) => opcode switch
    {
        0 => "DisplayFrame",
        1 => "PlayAnimSound",
        2 => "GoToItem",
        3 => "DisplayRandomFrame",
        4 => "SleepRandomDuration",
        5 => "PlayStockSound",
        _ => "unknown",
    };

    private static string CommandOpName(byte op) => op switch
    {
        0 => "kCommandBegin",
        1 => "kCommandEnd",
        2 => "kScriptCall",
        3 => "kDialogCall",
        4 => "kSetInteractiveMode",
        5 => "kLocationGoTo",
        7 => "kWalkTo",
        8 => "kGameLoop",
        9 => "kScriptPause",
        10 => "kScriptPauseRandom",
        11 => "kScriptPauseSkippable",
        13 => "kScriptAbort",
        19 => "kRumbleScene",
        20 => "kFadeScene",
        21 => "kSwayScene",
        22 => "kLocationGoToNewCD",
        23 => "kGameEnd",
        24 => "kInventoryOpen",
        25 => "kFloatScene",
        26 => "kBookOfSecretsOpen",
        80 => "kDoNothing",
        82 => "kItem3DWalkTo",
        84 => "kItemLookAt",
        87 => "kItemEnable",
        88 => "kItemSetActivity",
        89 => "kItemSelectInInventory",
        92 => "kUseAnimHierarchy",
        93 => "kPlayAnimation",
        94 => "kScriptEnable",
        95 => "kShowPlay",
        96 => "kKnowledgeSetBoolean",
        100 => "kKnowledgeSetInteger",
        101 => "kKnowledgeAddInteger",
        103 => "kEnableFloorField",
        104 => "kPlayAnimScriptItem",
        105 => "kItemAnimFollowPath",
        107 => "kKnowledgeAssignBool",
        110 => "kKnowledgeAssignInteger",
        111 => "kLocationScrollTo",
        112 => "kSoundPlay",
        115 => "kKnowledgeSetIntRandom",
        117 => "kKnowledgeSubValue",
        118 => "kItemLookDirection",
        119 => "kStopPlayingSound",
        120 => "kLayerGoTo",
        121 => "kLayerEnable",
        122 => "kLocationScrollSet",
        123 => "kFullMotionVideoPlay",
        125 => "kAnimSetFrame",
        126 => "kKnowledgeAssignNegatedBool",
        127 => "kDiaryEnableEntry",
        128 => "kPATChangeTooltip",
        129 => "kSoundChange",
        130 => "kLightSetColor",
        131 => "kLightFollowPath",
        133 => "kItemPlaceDirection",
        134 => "kItemRotateDirection",
        135 => "kActivateTexture",
        136 => "kActivateMesh",
        137 => "kItem3DSetWalkTarget",
        139 => "kSpeakWithoutTalking",
        162 => "kIsOnFloorField",
        163 => "kIsItemEnabled",
        165 => "kIsScriptEnabled",
        166 => "kIsKnowledgeBooleanSet",
        170 => "kIsKnowledgeIntegerInRange",
        171 => "kIsKnowledgeIntegerAbove",
        172 => "kIsKnowledgeIntegerEqual",
        173 => "kIsKnowledgeIntegerLower",
        174 => "kIsScriptActive",
        175 => "kIsRandom",
        176 => "kIsAnimScriptItemReached",
        177 => "kIsItemOnPlace",
        179 => "kIsAnimPlaying",
        180 => "kIsItemActivity",
        183 => "kIsItemNearPlace",
        185 => "kIsAnimAtTime",
        187 => "kIsInventoryOpen",
        _ => $"op {op}",
    };

    /// <summary>
    /// Formats a per-record label for the tree header, using type-specific names for the subType
    /// where one is defined. Falls back to a plain "TypeName sub=N" for types whose subType is either
    /// unused or opaque (e.g. Light's light-kind selector, Speech's language variant).
    /// </summary>
    private static string FormatTypeLabel(byte typeId, byte subType)
    {
        string type = TypeName(typeId);
        string? sub = typeId switch
        {
            TypeItem => ItemSubTypeName(subType),
            TypeAnim => AnimSubTypeName(subType),
            TypeImage => ImageSubTypeName(subType),
            TypeLayer => LayerSubTypeName(subType),
            TypePath => PathSubTypeName(subType),
            TypeKnowledge => KnowledgeSubTypeName(subType),
            TypeScript => ScriptSubTypeName(subType),
            TypeCommand => CommandOpName(subType),
            0x1A => ContainerSubTypeName(subType),
            _ => null,
        };

        if (sub is null || sub == "unknown")
            return subType == 0 ? type : $"{type} sub={subType}";
        return $"{type}: {sub}";
    }

    private static string TypeName(byte typeId) => typeId switch
    {
        0x01 => "Root",
        0x02 => "Level",
        0x03 => "Location",
        0x04 => "Layer",
        0x05 => "Camera",
        0x06 => "Floor",
        0x07 => "FloorFace",
        0x08 => "Item",
        0x09 => "Script",
        0x0A => "AnimHierarchy",
        0x0B => "Anim",
        0x0C => "Direction",
        0x0D => "Image",
        0x0E => "AnimScript",
        0x0F => "AnimScriptItem",
        0x10 => "Sound",
        0x11 => "Path",
        0x12 => "FloorField",
        0x13 => "Bookmark",
        0x14 => "KnowledgeSet",
        0x15 => "Knowledge",
        0x16 => "Command",
        0x17 => "PATTable",
        0x1A => "Container",
        0x1B => "Dialog",
        0x1D => "Speech",
        0x1E => "Light",
        0x20 => "BonesMesh",
        0x21 => "Scroll",
        0x22 => "FMV",
        0x23 => "LipSync",
        0x24 => "AnimSoundTrigger",
        0x25 => "String",
        0x26 => "TextureSet",
        _ => $"0x{typeId:X2}",
    };
}
