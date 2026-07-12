using System.IO;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Services;

/// <summary>Progress event fired after each file has been considered (exported, skipped, or failed).</summary>
public sealed record BatchExportProgress(string RelativePath, BatchExportResult Result, int Index, int Total);

/// <summary>Per-file outcome from a batch export walk.</summary>
public enum BatchExportResult { Exported, SkippedUnsupported, Failed }

/// <summary>Summary of a completed batch export walk.</summary>
public sealed record BatchExportSummary(
    int ExportedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<string> FailedPaths);

/// <summary>
/// Walks a subtree of the loaded virtual file system and exports every file it knows how to convert into
/// a common format, preserving the source folder hierarchy under an output root directory.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>Images (.xmg, .tm) → one or more <c>.tga</c> files (TM files split by sub-image name).</description></item>
///   <item><description>Sounds (.ovs, .isn family) → <c>.wav</c>.</description></item>
///   <item><description>Models (.cir, .biff) → <c>.obj</c> + <c>.mtl</c> pair.</description></item>
///   <item><description>Everything else (video, xrc, ani, unknown types) is skipped with a
///     <see cref="BatchExportResult.SkippedUnsupported"/> event.</description></item>
/// </list>
/// </remarks>
public static class BatchExporter
{
    /// <summary>
    /// Recursively walks <paramref name="root"/> and exports every recognised file under
    /// <paramref name="outputDirectory"/>. Fires <paramref name="progress"/> once per file so callers can
    /// stream status to a progress bar. Every catchable per-file exception is caught and reported as a
    /// <see cref="BatchExportResult.Failed"/> event; the walk continues.
    /// </summary>
    public static BatchExportSummary ExportSubtree(
        FsNode root,
        VirtualFileSystem vfs,
        string outputDirectory,
        Action<BatchExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        int exported = 0, skipped = 0, failed = 0;
        var failedPaths = new List<string>();
        string rootPath = root.GetPath();

        // Pre-materialize the file list so we can report N-of-M progress. For a large TLJ install this is
        // still cheap next to the actual export work, which does the heavy decoding per file.
        var files = EnumerateFiles(root, vfs).ToList();
        int total = files.Count;
        int index = 0;

        foreach (FsNode file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            index++;
            string relativePath = RelativePath(rootPath, file.GetPath());
            string relativeDir = Path.GetDirectoryName(relativePath) ?? string.Empty;
            string destinationDir = Path.Combine(outputDirectory, SanitizeRelativePath(relativeDir));
            string stem = Path.GetFileNameWithoutExtension(file.Name);

            BatchExportResult result;
            try
            {
                Directory.CreateDirectory(destinationDir);
                result = ExportOne(file, vfs, destinationDir, stem);
            }
            catch
            {
                result = BatchExportResult.Failed;
            }

            switch (result)
            {
                case BatchExportResult.Exported: exported++; break;
                case BatchExportResult.SkippedUnsupported: skipped++; break;
                case BatchExportResult.Failed: failed++; failedPaths.Add(relativePath); break;
            }

            progress?.Invoke(new BatchExportProgress(relativePath, result, index, total));
        }

        return new BatchExportSummary(exported, skipped, failed, failedPaths);
    }

    private static BatchExportResult ExportOne(FsNode file, VirtualFileSystem vfs, string destinationDir, string stem)
    {
        string ext = Path.GetExtension(file.Name).ToLowerInvariant();

        switch (ext)
        {
            case ".xmg":
            {
                using Stream s = vfs.OpenFile(file);
                DecodedImage img = XmgDecoder.Decode(s);
                TgaWriter.Write(img, Path.Combine(destinationDir, SanitizeSegment(stem) + ".tga"));
                return BatchExportResult.Exported;
            }

            case ".tm":
            {
                using Stream s = vfs.OpenFile(file);
                IReadOnlyList<TmEntry> entries = TmDecoder.Decode(s, useMipMap: false);
                if (entries.Count == 0)
                    return BatchExportResult.SkippedUnsupported;

                if (entries.Count == 1)
                {
                    string subName = string.IsNullOrEmpty(entries[0].Name) ? stem : entries[0].Name;
                    TgaWriter.Write(entries[0].Image, Path.Combine(destinationDir, SanitizeSegment(subName) + ".tga"));
                }
                else
                {
                    string subFolder = Path.Combine(destinationDir, SanitizeSegment(stem));
                    Directory.CreateDirectory(subFolder);
                    int i = 0;
                    foreach (TmEntry entry in entries)
                    {
                        string subName = string.IsNullOrEmpty(entry.Name) ? $"image_{i}" : entry.Name;
                        TgaWriter.Write(entry.Image, Path.Combine(subFolder, SanitizeSegment(subName) + ".tga"));
                        i++;
                    }
                }

                return BatchExportResult.Exported;
            }

            case ".ovs":
            {
                using Stream s = vfs.OpenFile(file);
                using var outFile = new FileStream(Path.Combine(destinationDir, SanitizeSegment(stem) + ".wav"), FileMode.Create, FileAccess.Write);
                OggDecoder.DecodeToWav(s, outFile);
                return BatchExportResult.Exported;
            }

            case ".isn":
            case ".iss":
            case ".ssn":
            case ".sn":
            {
                using Stream s = vfs.OpenFile(file);
                using var outFile = new FileStream(Path.Combine(destinationDir, SanitizeSegment(stem) + ".wav"), FileMode.Create, FileAccess.Write);
                IsnDecoder.DecodeToWav(s, outFile);
                return BatchExportResult.Exported;
            }

            case ".cir":
            {
                CirModel model;
                using (Stream s = vfs.OpenFile(file))
                    model = CirDecoder.Decode(s);

                // Look for a same-folder ANI to use as the bind pose so exported CIR characters open
                // correctly in Blender instead of as a bone-local fragment cloud.
                AniAnimation? bindPose = TryFindNeighborAni(file, vfs);
                var options = new GlbWriteOptions
                {
                    BindPose = bindPose,
                    TextureResolver = ModelTextureResolver.Create(vfs, file),
                };
                GlbWriter.Write(model, Path.Combine(destinationDir, SanitizeSegment(stem) + ".glb"), options);
                return BatchExportResult.Exported;
            }

            case ".biff":
            {
                using Stream s = vfs.OpenFile(file);
                BiffMesh mesh = BiffMeshReader.Read(s);
                CirModel cir = BiffToCirAdapter.ToCirModel(mesh);
                // BIFF props have their transform baked into vertices already — no bind pose needed.
                GlbWriter.Write(cir, Path.Combine(destinationDir, SanitizeSegment(stem) + ".glb"));
                return BatchExportResult.Exported;
            }

            default:
                return BatchExportResult.SkippedUnsupported;
        }
    }

    /// <summary>
    /// Finds a same-folder <c>.ani</c> to use as the bind pose for <paramref name="cirNode"/>. Best-effort:
    /// picks the first one alphabetically. Returns <c>null</c> when there's no ANI next to the CIR.
    /// </summary>
    private static AniAnimation? TryFindNeighborAni(FsNode cirNode, VirtualFileSystem vfs)
    {
        if (cirNode.Parent is null)
            return null;

        FsNode? aniNode = vfs.GetFiles(cirNode.Parent)
            .FirstOrDefault(f => string.Equals(Path.GetExtension(f.Name), ".ani", StringComparison.OrdinalIgnoreCase));
        if (aniNode is null)
            return null;

        try
        {
            using Stream s = vfs.OpenFile(aniNode);
            return AniDecoder.Decode(s);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<FsNode> EnumerateFiles(FsNode node, VirtualFileSystem vfs)
    {
        if ((node.NodeType & FsNodeType.File) != 0)
        {
            yield return node;
            yield break;
        }

        // Walk the tree without recursion so extremely deep XARC nestings don't blow the stack.
        var stack = new Stack<FsNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            FsNode current = stack.Pop();

            foreach (FsNode file in vfs.GetFiles(current))
                yield return file;

            foreach (FsNode dir in vfs.GetDirectories(current))
                stack.Push(dir);
        }
    }

    private static string RelativePath(string rootPath, string filePath)
    {
        if (filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return filePath.Substring(rootPath.Length).TrimStart('\\', '/');
        return filePath.TrimStart('\\', '/');
    }

    private static string SanitizeRelativePath(string relativeDir)
    {
        if (string.IsNullOrEmpty(relativeDir))
            return string.Empty;

        string[] parts = relativeDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            parts[i] = SanitizeSegment(parts[i]);

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static string SanitizeSegment(string segment)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = segment.Length <= 256 ? stackalloc char[segment.Length] : new char[segment.Length];
        for (int i = 0; i < segment.Length; i++)
            buffer[i] = invalid.Contains(segment[i]) ? '_' : segment[i];

        string result = new(buffer);
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
