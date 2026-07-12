using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>Kind of scene layer. See <see cref="XrcSceneLayer"/>.</summary>
public enum XrcSceneLayerKind
{
    /// <summary>The full-frame background of the scene (Item subtype 8 = kItemBackground).</summary>
    Backdrop,

    /// <summary>A positioned overlay: Item subtypes 5/6 (Static/AnimatedProp) or 7 (BackgroundElement).</summary>
    Sprite,
}

/// <summary>
/// A single drawable layer extracted from an XRC display tree by <see cref="XrcSceneModel"/>. Layers
/// are returned in source order, which is also the intended draw order (later layers on top).
/// </summary>
public sealed record XrcSceneLayer(
    XrcSceneLayerKind Kind,
    string Name,
    string FileName,
    int X,
    int Y);

/// <summary>
/// Structural reader that walks an XRC display tree (same binary layout as <see cref="XrcDisplayDump"/>)
/// and produces a flat, z-ordered list of drawable layers for a static "as if walking into the room"
/// scene render.
/// </summary>
/// <remarks>
/// <para>
/// Each visible scene element is an <c>Item</c> resource (TypeId 0x08). Its subtype (tag1) selects a
/// field layout:
/// </para>
/// <list type="bullet">
/// <item><description>5 kItemStaticProp, 6 kItemAnimatedProp -> FloorPositionedImageItem: (x,y) at data offset 16.</description></item>
/// <item><description>7 kItemBackgroundElement -> ImageItem: (x,y) at data offset 12.</description></item>
/// <item><description>8 kItemBackground -> ImageItem: (x,y) at data offset 12. Drawn as the full-frame backdrop.</description></item>
/// </list>
/// <para>
/// Item visibility on scene entry combines two sources: (a) the Item's own <c>enabled</c> flag (first
/// 4 bytes of its payload), and (b) any item-enable command (opcode 87 with enable-value 1) reachable
/// from a game-event script (script subtype 4, run-event 0 or 1). We simulate the second source as a
/// linear scan of those scripts' descendant commands -- good enough for the common "enable X, enable
/// Y, ..." chains at scene entry; conditional branches are treated as "possibly enabled".
/// </para>
/// </remarks>
public static class XrcSceneModel
{
    private const byte TypeLayer = 0x04;
    private const byte TypeItem = 0x08;
    private const byte TypeScript = 0x09;
    private const byte TypeAnimation = 0x0b;
    private const byte TypeDirection = 0x0c;
    private const byte TypeImage = 0x0d;
    private const byte TypeCommand = 0x16;

    // Anim subtypes.
    private const byte AnimSubTypeImages = 1;
    private const byte AnimSubTypeProp = 2;
    private const byte AnimSubTypeVideo = 3;

    private const byte ScriptSubTypeGameEvent = 4;
    private const int ScriptRunEventOnGameLoop = 0;
    private const int ScriptRunEventOnEnterLocation = 1;

    private const byte CommandSubTypeItemEnable = 87;

    private const uint ArgTypeZeroShortcut = 0;
    private const uint ArgTypeInteger1 = 1;
    private const uint ArgTypeInteger2 = 2;
    private const uint ArgTypeResourceReference = 3;
    private const uint ArgTypeString = 4;

    /// <summary>
    /// Byte offset of the <c>(x, y)</c> pair within an Item's data payload for the subtypes that carry
    /// one. Returns -1 for subtypes with no 2D position:
    /// <list type="bullet">
    ///   <item><description>5 (StaticProp), 6 (AnimatedProp): 16 bytes of preamble then (x, y).</description></item>
    ///   <item><description>7 (BackgroundElement), 8 (Background): 12 bytes of preamble then (x, y).</description></item>
    /// </list>
    /// </summary>
    internal static int ItemPositionOffset(byte itemSubType) => itemSubType switch
    {
        5 or 6 => 16,
        7 or 8 => 12,
        _ => -1,
    };

    /// <summary>
    /// Diagnostic view of every Item this reader considered from an XRC scene: subtype, index, name,
    /// authored <c>enabled</c> flag, position, and the first asset filename found in its subtree. Meant
    /// for troubleshooting the "why is X visible / not visible" question -- not part of the render path.
    /// </summary>
    public sealed record ItemDiagnostic(
        int SubType,
        int Index,
        string Name,
        bool XrcEnabled,
        bool EnabledByOnEnterScript,
        int? X,
        int? Y,
        string? AssetFile,
        int DataLength,
        IReadOnlyList<AnimDiagnostic> Anims);

    /// <summary>One Anim record under an Item, with enough detail to see which frames it exposes.</summary>
    public sealed record AnimDiagnostic(
        int SubType,
        int Index,
        string Name,
        int? Activity,
        int? NumFrames,
        IReadOnlyList<DirectionDiagnostic> Directions,
        string? VideoFile);

    public sealed record DirectionDiagnostic(
        int Index,
        int ImageCount,
        int LowestImageIndex,
        int HighestImageIndex,
        string? LowestImageFile,
        IReadOnlyList<(int Index, string File)> Images);

    /// <summary>Row-per-kItemEnable in the XRC. Useful for spotting where an ambient prop actually gets flipped on.</summary>
    public sealed record ItemEnableCall(
        string ScriptName,
        int ScriptSubType,
        int? ScriptRunEvent,
        int EnableValue,
        string TargetName,
        int[] TargetPathTypes,
        int[] TargetPathIndices);

    /// <summary>
    /// Combined diagnostic for a scene: every Item with its resolved state, plus every kItemEnable
    /// command anywhere in the tree with its target and enable value. Ships two views because "why is
    /// X not visible" usually needs both -- what my parser sees the Item as, and what commands (if any)
    /// touch it.
    /// </summary>
    public sealed record SceneDiagnostics(
        IReadOnlyList<ItemDiagnostic> Items,
        IReadOnlyList<ItemEnableCall> ItemEnableCalls);

    /// <summary>
    /// Walks the same tree <see cref="Read"/> walks and returns both the per-Item diagnostic rows and
    /// every kItemEnable command discovered anywhere in the tree, so a caller can cross-reference which
    /// items are targeted by which scripts.
    /// </summary>
    public static SceneDiagnostics Diagnose(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var roots = new List<RawRecord>();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
            roots.Add(ReadRecord(reader));

        var enabledOnEnter = new HashSet<RawRecord>();
        foreach (RawRecord root in roots)
            CollectOnEnterEnabledItems(root, root, enabledOnEnter);

        var rows = new List<ItemDiagnostic>();
        foreach (RawRecord root in roots)
            DiagnoseVisit(root, enabledOnEnter, rows);

        var calls = new List<ItemEnableCall>();
        foreach (RawRecord root in roots)
            CollectItemEnableCalls(root, root, currentScript: null, calls);

        return new SceneDiagnostics(rows, calls);
    }

    private static void CollectItemEnableCalls(RawRecord treeRoot, RawRecord record, RawRecord? currentScript, List<ItemEnableCall> calls)
    {
        RawRecord? nextScript = record.TypeId == TypeScript ? record : currentScript;

        if (record.TypeId == TypeCommand
            && record.Tag1 == CommandSubTypeItemEnable
            && TryReadItemEnableCommand(record.Data, out int[] refTypes, out int[] refIndices, out int enableValue))
        {
            RawRecord? target = ResolveItemReference(treeRoot, refTypes, refIndices);
            int? runEvent = null;
            if (nextScript is not null && nextScript.Data.Length >= 8)
                runEvent = BitConverter.ToInt32(nextScript.Data, 4);

            calls.Add(new ItemEnableCall(
                ScriptName: nextScript?.Name ?? "<no-script-parent>",
                ScriptSubType: nextScript?.Tag1 ?? -1,
                ScriptRunEvent: runEvent,
                EnableValue: enableValue,
                TargetName: target?.Name ?? "<unresolved>",
                TargetPathTypes: refTypes,
                TargetPathIndices: refIndices));
        }

        foreach (RawRecord child in record.Children)
            CollectItemEnableCalls(treeRoot, child, nextScript, calls);
    }

    private static void DiagnoseVisit(RawRecord record, HashSet<RawRecord> enabledOnEnter, List<ItemDiagnostic> rows)
    {
        if (record.TypeId == TypeItem)
        {
            int positionOffset = ItemPositionOffset(record.Tag1);
            bool xrcEnabled = record.Data.Length >= 4 && BitConverter.ToInt32(record.Data, 0) != 0;
            int? x = null, y = null;
            if (positionOffset >= 0 && record.Data.Length >= positionOffset + 8)
            {
                x = BitConverter.ToInt32(record.Data, positionOffset);
                y = BitConverter.ToInt32(record.Data, positionOffset + 4);
            }

            var animList = new List<RawRecord>();
            CollectAnims(record, animList);
            var animDiags = new List<AnimDiagnostic>();
            foreach (RawRecord a in animList)
                animDiags.Add(BuildAnimDiagnostic(a));

            rows.Add(new ItemDiagnostic(
                SubType: record.Tag1,
                Index: record.Index,
                Name: record.Name,
                XrcEnabled: xrcEnabled,
                EnabledByOnEnterScript: enabledOnEnter.Contains(record),
                X: x,
                Y: y,
                AssetFile: FindFirstAsset(record)?.FileName,
                DataLength: record.Data.Length,
                Anims: animDiags));
        }

        foreach (RawRecord child in record.Children)
            DiagnoseVisit(child, enabledOnEnter, rows);
    }

    /// <summary>
    /// Reads a scene from <paramref name="stream"/> (the full contents of a display-XRC entry). Returns
    /// the flat, ordered list of visible drawable layers.
    /// </summary>
    public static IReadOnlyList<XrcSceneLayer> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var roots = new List<RawRecord>();

        while (reader.BaseStream.Position < reader.BaseStream.Length)
            roots.Add(ReadRecord(reader));

        // Pass 1: simulate on-enter scripts to collect items enabled at scene entry.
        var enabledOnEnter = new HashSet<RawRecord>();
        foreach (RawRecord root in roots)
            CollectOnEnterEnabledItems(root, root, enabledOnEnter);

        // Pass 2: emit layers for every Item whose visibility is on (via XRC flag or script).
        var layers = new List<XrcSceneLayer>();
        foreach (RawRecord root in roots)
            Visit(root, layers, enabledOnEnter);

        return layers;
    }

    private static void Visit(RawRecord record, List<XrcSceneLayer> layers, HashSet<RawRecord> enabledOnEnter)
    {
        if (record.TypeId == TypeItem)
            TryEmitItemLayer(record, layers, enabledOnEnter);

        foreach (RawRecord child in record.Children)
            Visit(child, layers, enabledOnEnter);
    }

    private static void TryEmitItemLayer(RawRecord item, List<XrcSceneLayer> layers, HashSet<RawRecord> enabledOnEnter)
    {
        int positionOffset = ItemPositionOffset(item.Tag1);
        if (positionOffset < 0 || item.Data.Length < positionOffset + 8)
            return;

        // First 4 bytes of every Item payload are `enabled` (uint32 bool). Combined with the on-enter
        // / on-loop script scan, this approximates "what the player sees when they walk into the room"
        // without simulating the full game state machine.
        bool xrcEnabled = BitConverter.ToInt32(item.Data, 0) != 0;
        if (!xrcEnabled && !enabledOnEnter.Contains(item))
            return;

        int x = BitConverter.ToInt32(item.Data, positionOffset);
        int y = BitConverter.ToInt32(item.Data, positionOffset + 4);

        AssetRef? asset = FindFirstAsset(item);
        if (asset is null)
            return;

        // Actual draw position is item.pos - image.hotspot. The hotspot lives in each Image record's
        // payload right after the filename; it anchors the frame so successive frames of an animation
        // can stay visually aligned even if their image sizes differ.
        int drawX = x - asset.Value.HotspotX;
        int drawY = y - asset.Value.HotspotY;

        XrcSceneLayerKind kind = item.Tag1 == 8 ? XrcSceneLayerKind.Backdrop : XrcSceneLayerKind.Sprite;
        layers.Add(new XrcSceneLayer(kind, item.Name, asset.Value.FileName, drawX, drawY));
    }

    /// <summary>
    /// Finds the filename representing the Item's default rendered visual (frame 0 of its "passive"
    /// animation). The engine binds every non-model scene item to activity value 1 at load time, so
    /// the animation we want is the one whose first field (<c>activity</c>) equals <c>1</c>.
    /// </summary>
    private static AssetRef? FindFirstAsset(RawRecord item)
    {
        var anims = new List<RawRecord>();
        CollectAnims(item, anims);

        // Prefer the anim whose activity == 1 (the passive/idle state). Fall back to the first anim of
        // a type we know how to interpret. This matters when an item has multiple Anims for different
        // activities -- picking arbitrarily gets a mid-action frame instead of the resting pose.
        RawRecord? passive = anims.Find(a => a.Data.Length >= 4 && BitConverter.ToInt32(a.Data, 0) == 1);
        if (passive is not null)
        {
            AssetRef? a = FindAnimAssetFile(passive);
            if (a is not null) return a;
        }

        foreach (RawRecord anim in anims)
        {
            AssetRef? a = FindAnimAssetFile(anim);
            if (a is not null) return a;
        }

        // No Anim found or none had a usable asset -- fall back to any direct child Image (rare).
        foreach (RawRecord child in item.Children)
        {
            if (child.TypeId == TypeImage)
            {
                var (file, consumed) = XrcDisplayDump.ReadDataString(child.Data, 0);
                if (!string.IsNullOrEmpty(file))
                {
                    int hx = 0, hy = 0;
                    if (child.Data.Length >= consumed + 8)
                    {
                        hx = BitConverter.ToInt32(child.Data, consumed);
                        hy = BitConverter.ToInt32(child.Data, consumed + 4);
                    }
                    return new AssetRef(file, hx, hy);
                }
            }
        }

        return null;
    }

    private static AnimDiagnostic BuildAnimDiagnostic(RawRecord anim)
    {
        int? activity = anim.Data.Length >= 4 ? BitConverter.ToInt32(anim.Data, 0) : null;
        int? numFrames = anim.Data.Length >= 8 ? BitConverter.ToInt32(anim.Data, 4) : null;
        string? videoFile = anim.Tag1 == AnimSubTypeVideo
            ? (XrcDisplayDump.ReadDataString(anim.Data, 8).Value is { Length: > 0 } s ? s : null)
            : null;

        var dirs = new List<DirectionDiagnostic>();
        foreach (RawRecord child in anim.Children)
        {
            if (child.TypeId != TypeDirection) continue;

            int lo = int.MaxValue, hi = int.MinValue, count = 0;
            RawRecord? loRec = null;
            var images = new List<(int, string)>();
            foreach (RawRecord img in child.Children)
            {
                if (img.TypeId != TypeImage) continue;
                count++;
                if (img.Index < lo) { lo = img.Index; loRec = img; }
                if (img.Index > hi) hi = img.Index;
                var (imgFile, _) = XrcDisplayDump.ReadDataString(img.Data, 0);
                images.Add((img.Index, imgFile ?? "-"));
            }
            images.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            string? loFile = null;
            if (loRec is not null)
            {
                var (f, _) = XrcDisplayDump.ReadDataString(loRec.Data, 0);
                if (!string.IsNullOrEmpty(f)) loFile = f;
            }

            dirs.Add(new DirectionDiagnostic(
                Index: child.Index,
                ImageCount: count,
                LowestImageIndex: count == 0 ? -1 : lo,
                HighestImageIndex: count == 0 ? -1 : hi,
                LowestImageFile: loFile,
                Images: images));
        }

        return new AnimDiagnostic(anim.Tag1, anim.Index, anim.Name, activity, numFrames, dirs, videoFile);
    }

    /// <summary>Collects every <c>Anim</c> record in <paramref name="record"/>'s subtree, without descending into nested Items.</summary>
    private static void CollectAnims(RawRecord record, List<RawRecord> anims)
    {
        foreach (RawRecord child in record.Children)
        {
            if (child.TypeId == TypeItem) continue;
            if (child.TypeId == TypeAnimation) anims.Add(child);
            CollectAnims(child, anims);
        }
    }

    /// <summary>
    /// The resolved default asset for an Item: the filename plus its per-image hotspot. Draw position
    /// on screen is <c>item.pos - hotspot</c>.
    /// </summary>
    private readonly record struct AssetRef(string FileName, int HotspotX, int HotspotY);

    /// <summary>
    /// Extracts an Anim's default-frame asset. Payload layout:
    /// - Anim base: uint32 activity + uint32 numFrames = 8 bytes.
    /// - Video anim (subtype 3): base + string smackerFile at offset 8. Videos have no per-frame
    ///   hotspot in the Anim itself.
    /// - Image anim (subtype 1) / Prop anim (subtype 2): frame data lives in child Direction -> Image
    ///   records; recurse into the lowest-indexed Direction and pick its lowest-indexed Image, whose
    ///   payload starts with `filename : string` immediately followed by `hotspot : point` (u32 x, u32 y).
    /// </summary>
    private static AssetRef? FindAnimAssetFile(RawRecord anim)
    {
        if (anim.Tag1 == AnimSubTypeVideo)
        {
            var (file, _) = XrcDisplayDump.ReadDataString(anim.Data, 8);
            return string.IsNullOrEmpty(file) ? null : new AssetRef(file, 0, 0);
        }

        if (anim.Tag1 is AnimSubTypeImages or AnimSubTypeProp)
        {
            RawRecord? firstDirection = null;
            foreach (RawRecord child in anim.Children)
            {
                if (child.TypeId != TypeDirection) continue;
                if (firstDirection is null || child.Index < firstDirection.Index)
                    firstDirection = child;
            }

            if (firstDirection is null) return null;

            RawRecord? firstImage = null;
            foreach (RawRecord grand in firstDirection.Children)
            {
                if (grand.TypeId != TypeImage) continue;
                if (firstImage is null || grand.Index < firstImage.Index)
                    firstImage = grand;
            }

            if (firstImage is null) return null;

            var (file, consumed) = XrcDisplayDump.ReadDataString(firstImage.Data, 0);
            if (string.IsNullOrEmpty(file)) return null;

            int hotspotX = 0, hotspotY = 0;
            if (firstImage.Data.Length >= consumed + 8)
            {
                hotspotX = BitConverter.ToInt32(firstImage.Data, consumed);
                hotspotY = BitConverter.ToInt32(firstImage.Data, consumed + 4);
            }

            return new AssetRef(file, hotspotX, hotspotY);
        }

        return null;
    }

    // -------------------------------------------------------------------
    // On-enter script simulation
    // -------------------------------------------------------------------

    /// <summary>
    /// Walks <paramref name="record"/>'s subtree looking for on-enter Scripts. When one is found, its
    /// entire subtree is scanned for <c>kItemEnable</c> commands with an enable-value of 1; the
    /// referenced Item records (resolved against <paramref name="treeRoot"/>) join
    /// <paramref name="enabledOnEnter"/>.
    /// </summary>
    private static void CollectOnEnterEnabledItems(RawRecord treeRoot, RawRecord record, HashSet<RawRecord> enabledOnEnter)
    {
        if (IsOnEnterScript(record))
            CollectItemEnables(treeRoot, record, enabledOnEnter);

        foreach (RawRecord child in record.Children)
            CollectOnEnterEnabledItems(treeRoot, child, enabledOnEnter);
    }

    private static bool IsOnEnterScript(RawRecord record)
    {
        // Script payload: uint32 type, uint32 runEvent, uint32 minChapter, uint32 maxChapter,
        // uint32 shouldResetGameSpeed. Two runEvents matter for "state at scene entry":
        //   runEvent==1 fires on entry -- one-shot setup.
        //   runEvent==0 fires every loop -- how ambient looping props (air conditioner, passing
        //   hovercar, etc.) get enabled. Their enable/disable toggles cycle the animation, but for a
        //   preview we just want to know they end up visible at some point, so we treat any enable=1
        //   in either script kind as "visible".
        // Excluded: script subtype 5 (player-action) and 6 (dialog) -- those respond to player clicks
        // or dialog choices, not scene entry.
        if (record.TypeId != TypeScript || record.Tag1 != ScriptSubTypeGameEvent || record.Data.Length < 8)
            return false;

        int runEvent = BitConverter.ToInt32(record.Data, 4);
        return runEvent == ScriptRunEventOnEnterLocation || runEvent == ScriptRunEventOnGameLoop;
    }

    private static void CollectItemEnables(RawRecord treeRoot, RawRecord record, HashSet<RawRecord> enabledOnEnter)
    {
        if (record.TypeId == TypeCommand && record.Tag1 == CommandSubTypeItemEnable
            && TryReadItemEnableCommand(record.Data, out int[] refTypes, out int[] refIndices, out int enableValue)
            && enableValue == 1)
        {
            RawRecord? target = ResolveItemReference(treeRoot, refTypes, refIndices);
            if (target is not null)
                enabledOnEnter.Add(target);
        }

        foreach (RawRecord child in record.Children)
            CollectItemEnables(treeRoot, child, enabledOnEnter);
    }

    /// <summary>
    /// Best-effort parse of an item-enable command payload. Layout: uint32 argCount, then per-arg
    /// (uint32 type + typed value). The command uses argument 1 as the target resource reference and
    /// argument 2 as the enable value, so we skip argument 0.
    /// </summary>
    private static bool TryReadItemEnableCommand(byte[] data, out int[] refTypes, out int[] refIndices, out int enableValue)
    {
        refTypes = [];
        refIndices = [];
        enableValue = 0;

        int offset = 0;
        if (!TryReadUInt32(data, ref offset, out uint argCount) || argCount < 3)
            return false;

        // Argument 0 -- skip.
        if (!TrySkipArgument(data, ref offset))
            return false;

        // Argument 1 -- resource reference to the target item.
        if (!TryReadUInt32(data, ref offset, out uint arg1Type) || arg1Type != ArgTypeResourceReference)
            return false;
        if (!TryReadResourceReference(data, ref offset, out refTypes, out refIndices))
            return false;

        // Argument 2 -- integer enable value (0 disable, 1 enable, 2 toggle).
        if (!TryReadUInt32(data, ref offset, out uint arg2Type))
            return false;

        switch (arg2Type)
        {
            case ArgTypeZeroShortcut:
                enableValue = 0;
                return true;
            case ArgTypeInteger1:
            case ArgTypeInteger2:
                return TryReadInt32(data, ref offset, out enableValue);
            default:
                return false;
        }
    }

    private static bool TrySkipArgument(byte[] data, ref int offset)
    {
        if (!TryReadUInt32(data, ref offset, out uint type))
            return false;

        switch (type)
        {
            case ArgTypeZeroShortcut:
                return true;
            case ArgTypeInteger1:
            case ArgTypeInteger2:
                return TryReadUInt32(data, ref offset, out _);
            case ArgTypeResourceReference:
                return TryReadResourceReference(data, ref offset, out _, out _);
            case ArgTypeString:
                if (!TryReadUInt16(data, ref offset, out ushort len))
                    return false;
                if (offset + len > data.Length)
                    return false;
                offset += len;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadResourceReference(byte[] data, ref int offset, out int[] types, out int[] indices)
    {
        types = [];
        indices = [];

        if (!TryReadUInt32(data, ref offset, out uint count))
            return false;
        if (count > 32) // Sanity cap -- paths are only a handful of levels deep in practice.
            return false;

        var t = new int[count];
        var i = new int[count];
        for (int k = 0; k < count; k++)
        {
            if (offset + 3 > data.Length)
                return false;
            t[k] = data[offset++];
            i[k] = BitConverter.ToUInt16(data, offset);
            offset += 2;
        }

        types = t;
        indices = i;
        return true;
    }

    /// <summary>
    /// Resolves a resource-reference-style path to an Item record in our tree. We match the tail of
    /// the path (Layer -> Item) since the leading path elements (Root/Level/Location) point at layers
    /// of the tree that may live above our XRC's root and aren't meaningful here.
    /// </summary>
    private static RawRecord? ResolveItemReference(RawRecord treeRoot, int[] refTypes, int[] refIndices)
    {
        if (refTypes.Length == 0 || refTypes[^1] != TypeItem)
            return null;

        int itemIndex = refIndices[^1];
        int layerIndex = refTypes.Length >= 2 && refTypes[^2] == TypeLayer ? refIndices[^2] : -1;

        return FindMatchingItem(treeRoot, layerIndex, itemIndex, currentLayerIndex: -1);
    }

    private static RawRecord? FindMatchingItem(RawRecord record, int layerIndex, int itemIndex, int currentLayerIndex)
    {
        int layerScope = record.TypeId == TypeLayer ? record.Index : currentLayerIndex;

        if (record.TypeId == TypeItem
            && record.Index == itemIndex
            && (layerIndex < 0 || layerScope == layerIndex))
        {
            return record;
        }

        foreach (RawRecord child in record.Children)
        {
            RawRecord? hit = FindMatchingItem(child, layerIndex, itemIndex, layerScope);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    // -------------------------------------------------------------------
    // Low-level record reading
    // -------------------------------------------------------------------

    private static RawRecord ReadRecord(BinaryReader reader)
    {
        byte typeId = reader.ReadByte();
        byte tag1 = reader.ReadByte();
        ushort index = reader.ReadUInt16();
        ushort nameLen = reader.ReadUInt16();
        string name = nameLen > 0 ? Encoding.Latin1.GetString(reader.ReadBytes(nameLen)) : string.Empty;
        int dataSize = reader.ReadInt32();
        byte[] data = dataSize > 0 ? reader.ReadBytes(dataSize) : [];
        ushort numChildren = reader.ReadUInt16();
        _ = reader.ReadUInt16(); // unknown3

        var children = new List<RawRecord>(numChildren);
        for (int i = 0; i < numChildren; i++)
            children.Add(ReadRecord(reader));

        return new RawRecord(typeId, tag1, index, name, data, children);
    }

    private static bool TryReadUInt32(byte[] data, ref int offset, out uint value)
    {
        if (offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToUInt32(data, offset);
        offset += 4;
        return true;
    }

    private static bool TryReadInt32(byte[] data, ref int offset, out int value)
    {
        if (offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToInt32(data, offset);
        offset += 4;
        return true;
    }

    private static bool TryReadUInt16(byte[] data, ref int offset, out ushort value)
    {
        if (offset + 2 > data.Length)
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToUInt16(data, offset);
        offset += 2;
        return true;
    }

    private sealed record RawRecord(byte TypeId, byte Tag1, ushort Index, string Name, byte[] Data, List<RawRecord> Children);
}
