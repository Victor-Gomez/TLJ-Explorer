using System.IO;
using System.Text;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>
/// Walks the loaded VFS's external mods folder, matches each file against the tree, and returns a
/// human-readable report. Used by the "Diagnose External Mods" menu item — invaluable for figuring out
/// why a mod that's on disk isn't lighting up in the UI (wrong folder, name typo, ambiguous match, etc.).
/// </summary>
public static class ExternalModsDiagnostic
{
    public static string Build(VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        var sb = new StringBuilder();

        sb.AppendLine("External Mods Diagnostic");
        sb.AppendLine("========================");
        sb.AppendLine($"Install:       {vfs.BaseDir}");
        sb.AppendLine($"External mods: {vfs.ExternalModsDir ?? "(not set)"}");
        sb.AppendLine($"Load mods:     {(vfs.LoadMods ? "enabled" : "disabled")}");
        sb.AppendLine();

        if (string.IsNullOrEmpty(vfs.ExternalModsDir))
        {
            sb.AppendLine("No external mods folder is selected — File → Select External Mods Folder... to point at one.");
            return sb.ToString();
        }

        if (!Directory.Exists(vfs.ExternalModsDir))
        {
            sb.AppendLine("⚠ External mods folder does not exist on disk. Pick another folder or restore this one.");
            return sb.ToString();
        }

        // Build the same index the recursive matcher uses.
        var byName = new Dictionary<string, List<FsNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (FsNode file in EnumerateFiles(vfs.Root))
        {
            AddToIndex(byName, file.Name, file);
            if (file.Name.EndsWith(".xmg", StringComparison.OrdinalIgnoreCase))
                AddToIndex(byName, Path.ChangeExtension(file.Name, ".png"), file);
        }

        int applied = 0, ambiguous = 0, orphan = 0;
        var appliedRows = new List<string>();
        var ambiguousRows = new List<string>();
        var orphanRows = new List<string>();

        foreach (string modFile in Directory.EnumerateFiles(vfs.ExternalModsDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(vfs.ExternalModsDir, modFile);
            string fileName = Path.GetFileName(modFile);

            if (!byName.TryGetValue(fileName, out List<FsNode>? matches) || matches.Count == 0)
            {
                orphan++;
                orphanRows.Add($"  {relative}");
                continue;
            }

            if (matches.Count > 1)
            {
                ambiguous++;
                ambiguousRows.Add($"  {relative}  →  ambiguous ({matches.Count} candidates: {string.Join(", ", matches.Select(m => m.GetPath()))})");
                continue;
            }

            FsNode target = matches[0];
            bool activeMod = string.Equals(target.ModPath, modFile, StringComparison.OrdinalIgnoreCase);
            string status = activeMod ? "active" : (string.IsNullOrEmpty(target.ModPath) ? "not attached (?)" : "shadowed by " + target.ModPath);
            applied++;
            appliedRows.Add($"  {relative}  →  {target.GetPath()}   [{status}]");
        }

        sb.AppendLine($"Files under external mods root: {applied + ambiguous + orphan}");
        sb.AppendLine($"  Attached to a VFS entry: {applied}");
        sb.AppendLine($"  Ambiguous (multiple candidates): {ambiguous}");
        sb.AppendLine($"  Orphan (no matching archive entry): {orphan}");
        sb.AppendLine();

        AppendSection(sb, "ATTACHED", appliedRows);
        AppendSection(sb, "AMBIGUOUS", ambiguousRows);
        AppendSection(sb, "ORPHAN", orphanRows);

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string header, List<string> rows)
    {
        if (rows.Count == 0)
            return;
        sb.Append(header).AppendLine(":");
        foreach (string row in rows)
            sb.AppendLine(row);
        sb.AppendLine();
    }

    private static void AddToIndex(Dictionary<string, List<FsNode>> index, string name, FsNode node)
    {
        if (!index.TryGetValue(name, out List<FsNode>? list))
        {
            list = [];
            index[name] = list;
        }
        list.Add(node);
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
