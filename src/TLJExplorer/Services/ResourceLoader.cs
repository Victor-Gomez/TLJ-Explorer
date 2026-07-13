using System.IO;
using System.Windows.Media.Imaging;
using TLJExplorer.Rendering;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Core.Settings;

namespace TLJExplorer.Services;

/// <summary>
/// Maps a file <see cref="FsNode"/> to a renderable <see cref="ResourceContent"/> by dispatching on its
/// extension to the appropriate decoder in <see cref="TLJExplorer.Core.Formats"/>. This is the one place in
/// the app that knows "which decoder handles which extension" -- the UI layer only ever deals with the
/// resulting <see cref="ResourceContent"/> shape.
/// </summary>
public static class ResourceLoader
{
    // Files at or below this size get slurped into memory once so decoders can read from a MemoryStream
    // instead of hammering the archive with millions of tiny per-byte reads. Above this cap we fall back
    // to the archive-window stream directly -- the decoder pays the streaming cost, but we don't blow up
    // working set on the occasional huge asset (large videos, etc).
    private const long InMemoryDecodeLimit = 32L * 1024 * 1024;

    /// <summary>
    /// Opens <paramref name="node"/> via the VFS, then pre-buffers into a <see cref="MemoryStream"/> when
    /// it's small enough. Fully in-memory reads are 10-100x cheaper for decoders that walk bytes one at a
    /// time; the archive-window stream stays fine for the giant assets that would blow up working set.
    /// </summary>
    private static Stream OpenBuffered(VirtualFileSystem vfs, FsNode node, VirtualFileSystem.OpenVariant variant)
    {
        Stream raw = vfs.OpenFile(node, variant);
        if (raw is MemoryStream || !raw.CanSeek || raw.Length > InMemoryDecodeLimit)
            return raw;

        try
        {
            int size = checked((int)raw.Length);
            var buffer = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = raw.Read(buffer, read, size - read);
                if (n <= 0) break;
                read += n;
            }
            return new MemoryStream(buffer, 0, read, writable: false, publiclyVisible: true);
        }
        finally
        {
            raw.Dispose();
        }
    }

    /// <summary>
    /// Loads and decodes <paramref name="node"/> according to its extension. Never throws: decode
    /// failures are captured and surfaced as an <see cref="ErrorResource"/> so the UI can show them
    /// inline instead of crashing.
    /// </summary>
    public static ResourceContent Load(
        FsNode node,
        VirtualFileSystem vfs,
        AppSettings settings,
        TempFileTracker tempFiles,
        VirtualFileSystem.OpenVariant variant = VirtualFileSystem.OpenVariant.Preferred)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tempFiles);

        try
        {
            using Stream stream = OpenBuffered(vfs, node, variant);
            string ext = Path.GetExtension(node.Name).ToLowerInvariant();

            return ext switch
            {
                ".xmg" => LoadXmg(node, stream),
                ".tm" => LoadTm(node, stream, settings),
                ".ovs" => LoadOvsSound(node, stream, tempFiles),
                ".isn" or ".iss" or ".ssn" or ".sn" => LoadIsnSound(node, stream, tempFiles),
                ".sss" => LoadVideo(stream, tempFiles, ".smk", "Smacker"),
                ".bbb" => LoadVideo(stream, tempFiles, ".bik", "Bink"),
                ".cir" => LoadCir(node, stream),
                ".ani" => LoadAni(node, stream),
                ".xrc" => new TextResource(XrcDisplayDump.DumpAsText(stream)),
                ".biff" => LoadBiff(node, stream),
                _ => LoadRaw(stream),
            };
        }
        catch (Exception ex)
        {
            return new ErrorResource($"Failed to load \"{node.GetPath()}\":\n{ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to load <paramref name="folder"/> as an XRC scene (backdrop + positioned sprites, composited
    /// into a single bitmap). Returns null when the folder isn't a scene folder -- i.e. it doesn't contain a
    /// file child named <c>&lt;folder&gt;.xrc</c>, or that XRC declares no drawable layers/backdrop. Never
    /// throws; decode failures become an <see cref="ErrorResource"/>.
    /// </summary>
    public static ResourceContent? LoadScene(FsNode folder, VirtualFileSystem vfs, AppSettings settings, TempFileTracker tempFiles)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tempFiles);

        string expectedName = folder.Name + ".xrc";
        FsNode? xrcNode = folder.Children.FirstOrDefault(c =>
            (c.NodeType & FsNodeType.File) != 0 &&
            c.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));

        if (xrcNode is null)
            return null;

        try
        {
            IReadOnlyList<XrcSceneLayer> layers;
            using (Stream stream = vfs.OpenFile(xrcNode))
                layers = XrcSceneModel.Read(stream);

            // Opt-in diagnostic dump: toggle "Dump Scene Diagnostics" in the Options menu to get a
            // per-scene table of every Item's subtype/enabled/position/asset plus all item-enable
            // script calls, written to %TEMP%\TLJExplorer_last_scene_items.txt for post-mortem
            // inspection. Off by default -- neither costs nor artifacts on typical runs.
            if (settings.DumpSceneDiagnostics)
            {
                XrcSceneModel.SceneDiagnostics diagnostics;
                using (Stream diagStream = vfs.OpenFile(xrcNode))
                    diagnostics = XrcSceneModel.Diagnose(diagStream);

                string diagPath = Path.Combine(Path.GetTempPath(), "TLJExplorer_last_scene_items.txt");
                File.WriteAllText(diagPath, FormatDiagnostics(folder.GetPath(), diagnostics));
            }

            if (layers.Count == 0)
                return null;

            SceneComposer.Composition? composition = SceneComposer.Compose(layers, folder, vfs, settings, tempFiles);
            return composition is null ? null : new SceneResource(composition.Base, composition.Overlays);
        }
        catch (Exception ex)
        {
            return new ErrorResource($"Failed to render scene \"{folder.GetPath()}\":\n{ex.Message}");
        }
    }

    /// <summary>
    /// Formats a scene's diagnostic rows as a human-readable table for the opt-in
    /// <c>TLJEXPLORER_DUMP_SCENE=1</c> post-mortem dump.
    /// </summary>
    private static string FormatDiagnostics(string scenePath, XrcSceneModel.SceneDiagnostics diag)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Scene: ").AppendLine(scenePath);
        sb.Append("Items: ").AppendLine(diag.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("subT  idx  enab  onEnter  pos              dataLen  asset                                       name");
        sb.AppendLine("----  ---  ----  -------  ---------------  -------  ------------------------------------------  -----------");
        foreach (XrcSceneModel.ItemDiagnostic row in diag.Items)
        {
            string pos = row.X is int x && row.Y is int y ? $"({x,5},{y,5})" : "(   ?,    ?)";
            sb.AppendFormat(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,4}  {1,3}  {2,-4}  {3,-7}  {4,-15}  {5,7}  {6,-42}  {7}\n",
                row.SubType,
                row.Index,
                row.XrcEnabled ? "yes" : "no",
                row.EnabledByOnEnterScript ? "yes" : "no",
                pos,
                row.DataLength,
                row.AssetFile ?? "-",
                row.Name);
        }

        sb.AppendLine();
        sb.AppendLine("Anims per item:");
        foreach (XrcSceneModel.ItemDiagnostic row in diag.Items)
        {
            if (row.Anims.Count == 0) continue;
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "  Item [{0}] \"{1}\":\n", row.Index, row.Name);
            foreach (XrcSceneModel.AnimDiagnostic a in row.Anims)
            {
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "    Anim subT={0} idx={1,-3} activity={2,-4} numFrames={3,-4} video={4,-14} \"{5}\"\n",
                    a.SubType, a.Index,
                    a.Activity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                    a.NumFrames?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                    a.VideoFile ?? "-",
                    a.Name);
                foreach (XrcSceneModel.DirectionDiagnostic d in a.Directions)
                {
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "      Dir idx={0} images={1} range=[{2}..{3}]\n",
                        d.Index, d.ImageCount, d.LowestImageIndex, d.HighestImageIndex);
                    foreach (var (imgIdx, imgFile) in d.Images)
                    {
                        sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                            "        [{0}] {1}\n", imgIdx, imgFile);
                    }
                }
            }
        }

        sb.AppendLine();
        sb.Append("kItemEnable calls: ").AppendLine(diag.ItemEnableCalls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine("scriptSubT  runEvent  enable  target                     script                     path");
        sb.AppendLine("----------  --------  ------  -------------------------  -------------------------  ----");
        foreach (XrcSceneModel.ItemEnableCall call in diag.ItemEnableCalls)
        {
            string path = string.Join(" -> ",
                Enumerable.Range(0, call.TargetPathTypes.Length)
                    .Select(i => $"({call.TargetPathTypes[i]}:{call.TargetPathIndices[i]})"));
            sb.AppendFormat(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,10}  {1,8}  {2,6}  {3,-25}  {4,-25}  {5}\n",
                call.ScriptSubType,
                call.ScriptRunEvent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                call.EnableValue,
                call.TargetName,
                call.ScriptName,
                path);
        }

        return sb.ToString();
    }

    /// <summary>Forces a raw hex-dump load regardless of extension -- backs the "Export Raw..." feature.</summary>
    public static TextResource LoadRawForced(FsNode node, VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(vfs);

        using Stream stream = vfs.OpenFile(node);
        return LoadRaw(stream);
    }

    private static ImageResource LoadXmg(FsNode node, Stream stream)
    {
        // Stark mods routinely ship .png files as overrides for .xmg archive entries (see the mod path
        // in the VFS scanner), so the bytes we're handed can be either format. Sniff and dispatch.
        DecodedImage image = PngToDecodedImage.DecodeXmgOrPng(stream, XmgDecoder.Decode);
        return new ImageResource([(node.DisplayName, image)]);
    }

    private static ImageResource LoadTm(FsNode node, Stream stream, AppSettings settings)
    {
        IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream, settings.ShowMipMaps);
        var images = entries
            .Select(e => (Name: string.IsNullOrEmpty(e.Name) ? node.DisplayName : e.Name, e.Image))
            .ToList();
        return new ImageResource(images);
    }

    private static SoundResource LoadOvsSound(FsNode node, Stream stream, TempFileTracker tempFiles)
    {
        // .ovs is an Ogg Vorbis stream wrapped verbatim, but WPF's MediaPlayer only plays whatever the
        // host OS's installed codecs support -- Ogg Vorbis usually isn't one of them. Decode it ourselves
        // (NVorbis, pure managed) straight to WAV so playback doesn't depend on system codecs at all.
        string tempPath = tempFiles.CreateTempFile(".wav");
        using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        {
            OggDecoder.DecodeToWav(stream, output);
        }

        return new SoundResource(tempPath, node.ExtendedInfo);
    }

    private static SoundResource LoadIsnSound(FsNode node, Stream stream, TempFileTracker tempFiles)
    {
        string tempPath = tempFiles.CreateTempFile(".wav");
        using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        {
            IsnDecoder.DecodeToWav(stream, output);
        }

        return new SoundResource(tempPath, node.ExtendedInfo);
    }

    private static ExternalVideoResource LoadVideo(Stream stream, TempFileTracker tempFiles, string extension, string kind)
    {
        string tempPath = tempFiles.CreateTempFile(extension);
        ContainerUnwrap.ExtractToFile(stream, tempPath);
        return new ExternalVideoResource(tempPath, kind);
    }

    private static ResourceContent LoadBiff(FsNode node, Stream stream)
    {
        // Try to decode as a prop mesh; fall back to the text dump for BIFF files that don't contain a
        // single MeshObjectTri (e.g. materials-only fragments, or other BIFF-shaped resources we don't
        // recognize yet).
        long startPos = stream.CanSeek ? stream.Position : 0;

        try
        {
            BiffMesh mesh = BiffMeshReader.Read(stream);
            CirModel cir = BiffToCirAdapter.ToCirModel(mesh);
            return new ModelResource(cir, node);
        }
        catch (FormatException)
        {
            if (stream.CanSeek)
                stream.Seek(startPos, SeekOrigin.Begin);
            return new TextResource(BiffDump.DumpAsText(stream));
        }
    }

    private static ModelResource LoadCir(FsNode node, Stream stream)
    {
        // Static rendering only: CIR has no stored bind-pose skeleton, so a "static" render is simply the
        // per-vertex blend of the two stored bone-local positions with every bone's transform implicitly
        // identity (see ModelRenderer.BuildVertexData). Animation playback (.ani) remains out of scope and
        // still shows a text dump via LoadAni below.
        CirModel model = CirDecoder.Decode(stream);
        return new ModelResource(model, node);
    }

    private static TextResource LoadAni(FsNode node, Stream stream)
    {
        AniAnimation animation = AniDecoder.Decode(stream);
        string dump = AniDecoder.DumpAsText(animation);

        var header =
            $"""
             ================================================================
             Skeletal Animation -- live playback preview not yet implemented.
             Showing decoded structure only.

             File: {node.GetPath()}
             Duration: {animation.MaxTime} ms
             Bone tracks: {animation.BoneAnims.Length}
             ================================================================


             """;

        return new TextResource(header + dump);
    }

    private static TextResource LoadRaw(Stream stream)
    {
        byte[] bytes = RawFormat.ReadAll(stream);
        return new TextResource(RawFormat.ToHexDump(bytes));
    }
}
