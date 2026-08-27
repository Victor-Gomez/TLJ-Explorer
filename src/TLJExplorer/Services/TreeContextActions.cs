using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>
/// Which tree context-menu entries apply to a given node. Kept as a plain value computed from the node
/// alone so the menu's visibility rules are unit-testable, rather than living as conditions scattered
/// across the click handlers -- where the only way to discover an action didn't apply was to invoke it
/// and read the message box that came back.
/// </summary>
public readonly record struct TreeContextActions(
    bool CopyPath,
    bool RevealInExplorer,
    bool PlayLocalized,
    bool ViewOriginal,
    bool CompareMod,
    bool ExtractAsMod,
    bool ExportItem,
    bool ExportRaw,
    bool BatchExportFolder)
{
    /// <summary>Nothing applies -- the menu has no reason to open (no node, or an unrecognised one).</summary>
    public bool IsEmpty =>
        !CopyPath && !RevealInExplorer && !PlayLocalized && !ViewOriginal &&
        !CompareMod && !ExtractAsMod && !ExportItem && !ExportRaw && !BatchExportFolder;

    /// <summary>True when at least one entry in the "mod" group applies, i.e. that group's separator earns its place.</summary>
    public bool HasModGroup => PlayLocalized || ViewOriginal || CompareMod || ExtractAsMod;

    /// <summary>True when at least one export entry applies.</summary>
    public bool HasExportGroup => ExportItem || ExportRaw || BatchExportFolder;

    public static TreeContextActions For(FsNode? node)
    {
        if (node is null)
            return default;

        bool isFile = (node.NodeType & FsNodeType.File) != 0;
        bool isDirectory = (node.NodeType & FsNodeType.Directory) != 0 || (node.NodeType & FsNodeType.Root) != 0;
        bool inArchive = (node.NodeType & FsNodeType.InArchive) != 0;
        bool onDisk = !inArchive && !string.IsNullOrEmpty(node.ArchivePath);

        return new TreeContextActions(
            CopyPath: true,
            // Only loose files have a real path on disk to reveal; archive entries live inside a .xarc.
            RevealInExplorer: isFile && onDisk,
            PlayLocalized: isFile && FindLocalizedSibling(node) is not null,
            // Both of these compare against a mod override, so they need one to exist.
            ViewOriginal: isFile && node.HasMod,
            CompareMod: isFile && node.HasMod,
            // Extracting only means something for archived entries -- a loose file is already editable
            // in place, and one that already has a mod override has somewhere to edit already.
            ExtractAsMod: isFile && inArchive && !node.HasMod,
            ExportItem: isFile,
            ExportRaw: isFile,
            BatchExportFolder: isDirectory);
    }

    /// <summary>
    /// The sibling entry with the same name but the opposite locale flag, or <see langword="null"/> when
    /// this entry has no localised counterpart. English variants carry <c>IsLocalized=false</c>.
    /// </summary>
    public static FsNode? FindLocalizedSibling(FsNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Parent?.Children.FirstOrDefault(c =>
            (c.NodeType & FsNodeType.File) != 0 &&
            !ReferenceEquals(c, node) &&
            c.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase) &&
            c.IsLocalized != node.IsLocalized);
    }
}
