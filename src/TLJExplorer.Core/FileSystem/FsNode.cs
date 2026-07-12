using System.Text;

namespace TLJExplorer.Core.FileSystem;

/// <summary>
/// Classifies a <see cref="FsNode"/>. Flags-based so a node can be both a <see cref="File"/> and
/// <see cref="InArchive"/> at once.
/// </summary>
[Flags]
public enum FsNodeType
{
    None = 0,
    Root = 1,
    Directory = 2,
    File = 4,
    InArchive = 8,
}

/// <summary>
/// A single node (directory or file) in the in-memory virtual file system tree built by
/// <see cref="VirtualFileSystem"/>.
/// </summary>
public sealed class FsNode
{
    public FsNodeType NodeType { get; set; }

    /// <summary>Physical name: the folder name on disk, or the entry name inside a <c>.xarc</c> archive.</summary>
    public string Name { get; set; } = "";

    /// <summary>Display name if known (typically sourced from an <c>.xrc</c> file), else falls back to <see cref="Name"/>.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>Extra display lines, e.g. subtitle text for dialogue sound files.</summary>
    public string[]? ExtendedInfo { get; set; }

    /// <summary>Byte offset within the owning archive. Only meaningful when <see cref="FsNodeType.InArchive"/> is set.</summary>
    public long Offset { get; set; }

    /// <summary>Size in bytes (file nodes).</summary>
    public long Size { get; set; }

    /// <summary>
    /// The absolute path used to open this file's bytes: the owning <c>.xarc</c> archive's path for
    /// <see cref="FsNodeType.InArchive"/> nodes, or the physical file path on disk for loose files.
    /// Unused for directory nodes.
    /// </summary>
    public string? ArchivePath { get; set; }

    /// <summary>
    /// For nodes sourced from a <c>.xarc</c> entry: <c>true</c> if the archive marked the entry as
    /// localised (non-English), <c>false</c> otherwise. Meaningful only when <see cref="FsNodeType.InArchive"/>
    /// is set; ignored for directory / loose-file nodes.
    /// </summary>
    public bool IsLocalized { get; set; }

    /// <summary>
    /// Absolute disk path of a loose-file mod that overrides this entry, if any. Populated for archive
    /// entries whose parent folder has a matching file under <c>xarc\&lt;name&gt;</c> (ScummVM Stark's
    /// mod convention). <c>OpenFile</c> returns the mod when <see cref="VirtualFileSystem.LoadMods"/> is
    /// on; otherwise the archived original is served.
    /// </summary>
    public string? ModPath { get; set; }

    /// <summary>True when a modded override has been detected for this node.</summary>
    public bool HasMod => !string.IsNullOrEmpty(ModPath);

    public FsNode? Parent { get; set; }

    public List<FsNode> Children { get; } = [];

    public string DisplayName => FriendlyName ?? Name;

    /// <summary>Builds the full <c>\</c>-delimited path from the root down to this node.</summary>
    public string GetPath()
    {
        var segments = new List<string>();
        for (FsNode? node = this; node is not null && node.Parent is not null; node = node.Parent)
            segments.Add(node.Name);

        segments.Reverse();

        var sb = new StringBuilder("\\");
        sb.Append(string.Join('\\', segments));
        return sb.ToString();
    }
}
