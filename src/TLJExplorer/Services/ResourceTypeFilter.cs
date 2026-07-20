using System.IO;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>
/// A selectable "show only this kind of file" category for the tree's type filter, grouping the raw
/// extensions handled by <see cref="ResourceLoader"/> into the same buckets the app's viewers use
/// (image / sound / video / model / animation / scene-data). <see cref="Extensions"/> is <c>null</c>
/// for the "All Types" entry, meaning no filtering by extension. When <see cref="NodePredicate"/> is
/// non-null it fully replaces the extension check, letting a filter key off the surrounding folder
/// structure (see the "Scenes" entry, which surfaces the <c>&lt;folder&gt;.xrc</c> marker file that
/// identifies a folder as a renderable XRC scene).
/// </summary>
public sealed record ResourceTypeFilter(
    string Label,
    string[]? Extensions,
    string? IconResourceKey = null,
    Func<FsNode, bool>? NodePredicate = null)
{
    public static readonly ResourceTypeFilter All = new("All Types", null, "FolderClosedTypeIcon");

    public static readonly IReadOnlyList<ResourceTypeFilter> Categories =
    [
        All,
        new("Images (.xmg, .tm)", [".xmg", ".tm"], "ImageTypeIcon"),
        new("Sounds (.ovs, .isn)", [".ovs", ".isn", ".iss", ".ssn", ".sn"], "SoundTypeIcon"),
        new("Video (.sss, .bbb)", [".sss", ".bbb"], "VideoTypeIcon"),
        new("3D Models (.cir)", [".cir"], "ModelTypeIcon"),
        new("Animations (.ani)", [".ani"], "AnimationTypeIcon"),
        new("Scene Data (.xrc, .biff)", [".xrc", ".biff"], "DataTypeIcon"),
        new("Scenes", null, "DataTypeIcon", NodePredicate: IsSceneMarker),
    ];

    /// <summary>
    /// A folder is a scene iff it contains a <c>&lt;folderName&gt;.xrc</c> file (see
    /// <see cref="ResourceLoader.LoadScene"/>). We can't filter directly on folders through the
    /// existing BuildFiltered plumbing (which walks files and prunes empty branches), so instead
    /// key off that marker file: keep just the <c>.xrc</c> that shares its parent folder's name,
    /// and the containing folder survives the prune.
    /// </summary>
    private static bool IsSceneMarker(FsNode node)
    {
        if ((node.NodeType & FsNodeType.File) == 0 || node.Parent is null)
            return false;
        if (!node.Name.EndsWith(".xrc", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(node.Name, node.Parent.Name + ".xrc", StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(FsNode node)
    {
        if (NodePredicate is not null)
            return NodePredicate(node);

        if (Extensions is null)
            return true;

        string ext = Path.GetExtension(node.Name);
        return Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() => Label;
}
