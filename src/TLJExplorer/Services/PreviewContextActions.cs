using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>
/// Which preview-area context-menu entries apply to whatever is currently on the canvas. Same idea as
/// <see cref="TreeContextActions"/>, but keyed off the loaded <see cref="ResourceContent"/> rather than
/// the tree node: the canvas shows one of several very different viewers, and offering "Reset View" over
/// an image or "Zoom In" over a waveform is noise.
/// </summary>
public readonly record struct PreviewContextActions(
    bool Zoom,
    bool ResetView,
    bool ToggleWireframe,
    bool CopyText,
    bool OpenExternally,
    bool Export,
    bool ExportRaw,
    bool CopyPath,
    bool RevealInExplorer)
{
    /// <summary>Nothing is loaded, so the canvas has no menu worth opening.</summary>
    public bool IsEmpty =>
        !Zoom && !ResetView && !ToggleWireframe && !CopyText && !OpenExternally &&
        !Export && !ExportRaw && !CopyPath && !RevealInExplorer;

    /// <summary>True when the view-manipulation group has any entry, i.e. its separator earns its place.</summary>
    public bool HasViewGroup => Zoom || ResetView || ToggleWireframe || CopyText || OpenExternally;

    /// <summary>True when any export entry applies.</summary>
    public bool HasExportGroup => Export || ExportRaw;

    /// <param name="content">What the canvas currently shows, or <see langword="null"/> when it's empty.</param>
    /// <param name="node">The tree node the content came from, if any.</param>
    public static PreviewContextActions For(ResourceContent? content, FsNode? node)
    {
        if (content is null)
            return default;

        bool isFile = node is not null && (node.NodeType & FsNodeType.File) != 0;
        bool onDisk = node is not null
                      && (node.NodeType & FsNodeType.InArchive) == 0
                      && !string.IsNullOrEmpty(node.ArchivePath);

        // A scene is composed from a folder rather than decoded from one file, so there are no single-file
        // raw bytes to export -- but the composite itself is still exportable as an image.
        bool exportable = content is not ErrorResource;
        bool hasRawBytes = isFile && content is not (SceneResource or ErrorResource);

        return new PreviewContextActions(
            Zoom: content is ImageResource or SceneResource,
            ResetView: content is ModelResource,
            ToggleWireframe: content is ModelResource,
            CopyText: content is TextResource,
            OpenExternally: content is ExternalVideoResource,
            Export: exportable,
            ExportRaw: hasRawBytes,
            CopyPath: node is not null,
            RevealInExplorer: isFile && onDisk);
    }
}
