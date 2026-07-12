using System.IO;

namespace TLJExplorer.Services;

/// <summary>
/// A selectable "show only this kind of file" category for the tree's type filter, grouping the raw
/// extensions handled by <see cref="ResourceLoader"/> into the same buckets the app's viewers use
/// (image / sound / video / model / animation / scene-data). <see cref="Extensions"/> is <c>null</c>
/// for the "All Types" entry, meaning no filtering by extension.
/// </summary>
public sealed record ResourceTypeFilter(string Label, string[]? Extensions, string? IconResourceKey = null)
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
    ];

    public bool Matches(string fileName)
    {
        if (Extensions is null)
            return true;

        string ext = Path.GetExtension(fileName);
        return Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() => Label;
}
