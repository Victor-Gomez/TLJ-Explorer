using System.IO;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>
/// Materialises a tree entry as a real file on disk so it can be handed to the OS as a drag payload.
/// Dragging out of the app has to give the file manager an actual path, but the asset normally lives
/// inside a <c>.xarc</c> -- so it gets exported to a scratch directory first.
/// </summary>
public static class DragDropStaging
{
    /// <summary>
    /// Exports <paramref name="node"/> into a fresh folder under the app's scratch area and returns the
    /// resulting file path, or <see langword="null"/> if nothing could be produced.
    /// </summary>
    /// <remarks>
    /// Uses the same conversions as Batch Export (images to PNG, sounds to WAV, models to glTF binary) so
    /// a dragged-out file matches what the menu would have produced. Formats the batch exporter doesn't
    /// recognise fall back to the raw archive bytes under the original name, which is still more useful
    /// than refusing the drag.
    /// </remarks>
    public static string? Stage(FsNode node, VirtualFileSystem vfs, TempFileTracker tempFiles)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(tempFiles);

        if ((node.NodeType & FsNodeType.File) == 0)
            return null;

        // Each drag gets its own directory: the exported name comes from the source entry, and two drags
        // of same-named entries from different folders would otherwise collide. The tracker owns it, so
        // it's cleaned up with the rest of the scratch area on exit.
        string stagingDir = tempFiles.CreateTempDirectory();

        try
        {
            BatchExportSummary summary = BatchExporter.ExportSubtree(node, vfs, stagingDir);

            if (summary.ExportedCount > 0)
            {
                string? exported = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).FirstOrDefault();
                if (exported is not null)
                    return exported;
            }

            return StageRawBytes(node, vfs, stagingDir);
        }
        catch (Exception ex)
        {
            Log.Exception($"Failed to stage '{node.GetPath()}' for drag-out", ex);
            return null;
        }
    }

    /// <summary>Verbatim archive bytes under the entry's own name, for anything the batch exporter skips.</summary>
    private static string? StageRawBytes(FsNode node, VirtualFileSystem vfs, string stagingDir)
    {
        string destination = Path.Combine(stagingDir, SanitizeFileName(node.Name));

        using (Stream source = vfs.OpenFile(node))
        using (FileStream target = File.Create(destination))
        {
            source.CopyTo(target);
        }

        return destination;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) ? "item.bin" : name;
    }
}
