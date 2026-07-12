using System.IO;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>
/// A flat, whole-VFS catalog of every model (<c>.cir</c>), animation (<c>.ani</c>), and skin/texture
/// (<c>.tm</c>) file, used to populate the model viewer's Model/Skin/Animation dropdowns when the
/// "Include whole install" checkbox is on. Built once per <see cref="VirtualFileSystem"/> load (see
/// <see cref="Build"/>) and cached by the caller (<c>MainWindow</c>) -- NOT rebuilt per model selection.
/// </summary>
/// <remarks>
/// Only <c>.tm</c> counts as a "skin" here. <c>.xmg</c> is deliberately excluded even though it's also
/// a decodable image format elsewhere in this app: it's used for full-screen background pictures,
/// not character/model texture atlases, so it's not a valid alternate skin.
/// </remarks>
/// <remarks>
/// A full walk of the in-memory VFS tree is cheap (measured ~27 ms for 23,000 files), but should still
/// be built off the UI thread via <see cref="System.Threading.Tasks.Task.Run(Action)"/> so a scan of a
/// large install doesn't briefly stall input.
/// </remarks>
public sealed class ModelBrowseCatalog
{
    public IReadOnlyList<FsNode> Models { get; }

    public IReadOnlyList<FsNode> Animations { get; }

    public IReadOnlyList<FsNode> Skins { get; }

    private ModelBrowseCatalog(List<FsNode> models, List<FsNode> animations, List<FsNode> skins)
    {
        Models = models;
        Animations = animations;
        Skins = skins;
    }

    /// <summary>Walks the whole tree rooted at <paramref name="vfs"/>'s root once, bucketing every file by extension.</summary>
    public static ModelBrowseCatalog Build(VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        var models = new List<FsNode>();
        var animations = new List<FsNode>();
        var skins = new List<FsNode>();

        Walk(vfs, vfs.Root, models, animations, skins);

        return new ModelBrowseCatalog(models, animations, skins);
    }

    private static void Walk(VirtualFileSystem vfs, FsNode dir, List<FsNode> models, List<FsNode> animations, List<FsNode> skins)
    {
        foreach (FsNode file in vfs.GetFiles(dir))
        {
            switch (Path.GetExtension(file.Name).ToLowerInvariant())
            {
                case ".cir":
                    models.Add(file);
                    break;
                case ".ani":
                    animations.Add(file);
                    break;
                case ".tm":
                    skins.Add(file);
                    break;
            }
        }

        foreach (FsNode subDir in vfs.GetDirectories(dir))
            Walk(vfs, subDir, models, animations, skins);
    }
}
