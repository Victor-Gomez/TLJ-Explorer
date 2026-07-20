using System.IO;
using Avalonia.Media.Imaging;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Services;

/// <summary>
/// Resolves a <see cref="CirMaterial.TextureName"/> against the VFS and encodes the decoded texture as PNG
/// bytes, suitable for embedding via <see cref="GlbWriteOptions.TextureResolver"/>.
/// </summary>
/// <remarks>
/// Handled matches (case-insensitive), first hit wins:
/// <list type="number">
///   <item><description>An entry with the exact <c>TextureName</c> (e.g. <c>face.tm</c>).</description></item>
///   <item><description>An entry with the same stem but any of the known image extensions (<c>.tm</c>, <c>.xmg</c>).</description></item>
///   <item><description>A <c>.tm</c> sub-image whose named entry matches (some TMs bundle several sub-images).</description></item>
/// </list>
/// </remarks>
public static class ModelTextureResolver
{
    /// <summary>
    /// Returns a resolver bound to <paramref name="vfs"/> and to the neighbourhood around
    /// <paramref name="modelNode"/>. The neighbourhood is searched first (same folder as the CIR/BIFF),
    /// then the whole install as a fallback — mirrors how the model viewer's skin dropdown already scopes
    /// its search.
    /// </summary>
    public static Func<string, byte[]?> Create(VirtualFileSystem vfs, FsNode modelNode)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(modelNode);

        var cache = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        List<FsNode> neighbourhood = modelNode.Parent is null
            ? []
            : vfs.GetFiles(modelNode.Parent).ToList();

        return textureName =>
        {
            if (string.IsNullOrEmpty(textureName))
                return null;
            if (cache.TryGetValue(textureName, out byte[]? cached))
                return cached;

            byte[]? bytes = Resolve(vfs, modelNode, neighbourhood, textureName);
            cache[textureName] = bytes;
            return bytes;
        };
    }

    private static byte[]? Resolve(VirtualFileSystem vfs, FsNode modelNode, List<FsNode> neighbourhood, string textureName)
    {
        string stem = Path.GetFileNameWithoutExtension(textureName);
        string ext = Path.GetExtension(textureName);

        // Pass 1: sibling exact-name / stem+known-extension.
        foreach (FsNode candidate in neighbourhood)
        {
            if (Matches(candidate, textureName, stem, ext))
            {
                byte[]? bytes = TryEncodePng(vfs, candidate, stem);
                if (bytes is not null) return bytes;
            }
        }

        // Pass 2: whole-install walk. Deliberately cheap fallback for TMs kept in shared folders.
        foreach (FsNode file in EnumerateFiles(vfs.Root))
        {
            if (ReferenceEquals(file, modelNode))
                continue;
            if (Matches(file, textureName, stem, ext))
            {
                byte[]? bytes = TryEncodePng(vfs, file, stem);
                if (bytes is not null) return bytes;
            }
        }

        return null;
    }

    private static bool Matches(FsNode file, string textureName, string stem, string ext)
    {
        if ((file.NodeType & FsNodeType.File) == 0)
            return false;
        if (file.Name.Equals(textureName, StringComparison.OrdinalIgnoreCase))
            return true;

        string fileExt = Path.GetExtension(file.Name);
        if (fileExt is not (".tm" or ".TM" or ".xmg" or ".XMG"))
        {
            // Preserve original-extension exact matches (already handled above) but skip anything else.
            return false;
        }

        string fileStem = Path.GetFileNameWithoutExtension(file.Name);
        return fileStem.Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? TryEncodePng(VirtualFileSystem vfs, FsNode file, string preferredSubImageName)
    {
        try
        {
            using Stream s = vfs.OpenFile(file);
            string ext = Path.GetExtension(file.Name).ToLowerInvariant();

            DecodedImage? image = ext switch
            {
                ".xmg" => XmgDecoder.Decode(s),
                ".tm" => PickTmImage(TmDecoder.Decode(s, useMipMap: false), preferredSubImageName),
                _ => null,
            };
            if (image is null || image.Width <= 0 || image.Height <= 0)
                return null;

            return EncodePng(image);
        }
        catch
        {
            return null;
        }
    }

    private static DecodedImage? PickTmImage(IReadOnlyList<TmEntry> entries, string preferredSubImageName)
    {
        if (entries.Count == 0)
            return null;

        // TMs frequently bundle named sub-images (e.g. "face", "body"). Prefer the one whose name matches
        // the CIR material's referenced stem; otherwise fall back to the first sub-image.
        foreach (TmEntry e in entries)
        {
            if (!string.IsNullOrEmpty(e.Name) && e.Name.Equals(preferredSubImageName, StringComparison.OrdinalIgnoreCase))
                return e.Image;
        }

        return entries[0].Image;
    }

    private static byte[] EncodePng(DecodedImage image)
    {
        using Bitmap bitmap = PngWriter.ToBitmap(image);
        using var mem = new MemoryStream();
        bitmap.Save(mem);
        return mem.ToArray();
    }

    private static IEnumerable<FsNode> EnumerateFiles(FsNode node)
    {
        foreach (FsNode child in node.Children)
        {
            if ((child.NodeType & FsNodeType.File) != 0)
                yield return child;
            if ((child.NodeType & FsNodeType.Directory) != 0)
            {
                foreach (FsNode desc in EnumerateFiles(child))
                    yield return desc;
            }
        }
    }
}
