namespace TLJExplorer.Core.FileSystem;

/// <summary>
/// Builds and exposes an in-memory tree that mirrors a TLJ install's data layout: nested directories,
/// each optionally backed by a <c>&lt;dirname&gt;.xarc</c> archive (and an internal <c>.xrc</c> describing
/// friendly names/virtual sub-folders), plus loose files sitting on disk.
/// </summary>
public sealed class VirtualFileSystem
{
    private const long MaxLooseFileSize = 800L * 1024 * 1024;

    private static readonly string[] StaticSubfolders =
    [
        "diaryfmv",
        "diaryindexlocation",
        "diarylog",
        "diarypages",
        "loadsavelocation",
        "mainmenulocation",
        "optionlocation",
    ];

    public FsNode Root { get; }

    public string BaseDir { get; }

    /// <summary>
    /// Optional external mod-source folder — additional loose-file mods live here, mirroring the
    /// install's <c>&lt;archiveDir&gt;/xarc/&lt;name&gt;</c> layout. When set, external mod files take
    /// precedence over any equivalent file sitting inside the install itself. <c>null</c> when only the
    /// in-install <c>xarc/</c> folders are used.
    /// </summary>
    public string? ExternalModsDir { get; }

    /// <summary>
    /// When true, <see cref="OpenFile"/> serves <see cref="FsNode.ModPath"/> for entries that have a mod
    /// override. When false, the original archive bytes are returned even for modded entries. Off by
    /// default; the UI toggles this at runtime.
    /// </summary>
    public bool LoadMods { get; set; }

    private VirtualFileSystem(FsNode root, string baseDir, string? externalModsDir = null)
    {
        Root = root;
        BaseDir = baseDir;
        ExternalModsDir = externalModsDir;
    }

    /// <summary>
    /// Scans <paramref name="baseDir"/> (a TLJ install root) and builds the full virtual file system tree.
    /// </summary>
    /// <param name="scanCallback">Optional progress callback invoked with the physical path of each directory as it is entered.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="baseDir"/> does not look like a valid TLJ install (its root <c>.xrc</c> is missing, or
    /// doesn't declare the expected "LAIDBACK" root name).
    /// </exception>
    public static VirtualFileSystem Init(string baseDir, Action<string>? scanCallback = null, string? externalModsDir = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDir);

        string trimmedBaseDir = baseDir.TrimEnd('\\', '/');
        string? trimmedModsDir = string.IsNullOrWhiteSpace(externalModsDir)
            ? null
            : externalModsDir.TrimEnd('\\', '/');

        // The game always names the root archive/entry literally "x" (x.xarc / x.xrc),
        // regardless of what the install folder itself is named on disk.
        var root = new FsNode
        {
            NodeType = FsNodeType.Root | FsNodeType.Directory,
            Name = "x",
        };

        var fileRefs = new List<XrcFileRef>();
        Traverse(root, trimmedBaseDir, trimmedBaseDir, trimmedModsDir, scanCallback, fileRefs);

        if (!string.Equals(root.FriendlyName, "LAIDBACK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{trimmedBaseDir}' does not look like a valid The Longest Journey install: " +
                "its root .xrc was missing or did not declare the expected \"LAIDBACK\" root name.");
        }

        ApplyFileRefs(root, fileRefs);
        GraftStaticFolders(root, trimmedBaseDir);

        // External-mod post-pass: mirror-path matching (in AddLooseFiles) is precise but rigid — mods
        // like TLJHD organise files by category ("Global/xarc/*.png") in ways that don't match every
        // archive's on-disk relative path. As a fallback, walk the external mods folder recursively and
        // attach any file whose name (with png↔xmg swap) uniquely resolves to an archive entry.
        if (!string.IsNullOrEmpty(trimmedModsDir) && Directory.Exists(trimmedModsDir))
            ApplyExternalModsRecursive(root, trimmedModsDir);

        root.FriendlyName = "The Longest Journey";

        return new VirtualFileSystem(root, trimmedBaseDir, trimmedModsDir);
    }

    /// <summary>
    /// Walks <paramref name="externalModsDir"/> recursively and attaches every file it finds as a mod
    /// override for the matching archive entry (by exact name, or PNG-for-XMG stem swap). Applies only
    /// when the match is unambiguous — files whose names could resolve to multiple archive entries are
    /// skipped to avoid guessing. Nodes that already have a <see cref="FsNode.ModPath"/> from the
    /// stricter mirror-path scan are preserved.
    /// </summary>
    private static void ApplyExternalModsRecursive(FsNode root, string externalModsDir)
    {
        // Build a name→nodes index that indexes each file under BOTH its real name AND (for .xmg files)
        // the swapped .png variant. That way a single Dictionary lookup handles both the exact-name and
        // PNG-for-XMG cases.
        var byName = new Dictionary<string, List<FsNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (FsNode fileNode in EnumerateFiles(root))
        {
            AddToIndex(byName, fileNode.Name, fileNode);

            foreach (string modName in SwapArchiveToModCandidates(fileNode.Name))
                AddToIndex(byName, modName, fileNode);
        }

        foreach (string modFile in Directory.EnumerateFiles(externalModsDir, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(modFile);
            if (!byName.TryGetValue(fileName, out List<FsNode>? matches) || matches.Count == 0)
                continue;

            // Multiple candidates would require the mod's own directory structure to disambiguate — bail
            // out rather than pick one at random. Users can rely on the strict mirror-path scan for the
            // ambiguous cases.
            if (matches.Count > 1)
                continue;

            FsNode target = matches[0];
            if (!string.IsNullOrEmpty(target.ModPath))
                continue; // A stricter mirror-path scan already applied a mod; don't overwrite.

            target.ModPath = modFile;
        }
    }

    /// <summary>
    /// Recursively populates <paramref name="node"/> (a directory node) from its archive at
    /// <c>physicalPath\node.Name.xarc</c>, if present, plus any loose files under <c>physicalPath\xarc\</c>.
    /// </summary>
    private static void Traverse(FsNode node, string physicalPath, string installRoot, string? externalModsDir, Action<string>? scanCallback, List<XrcFileRef> fileRefs)
    {
        scanCallback?.Invoke(physicalPath);

        string xarcPath = Path.Combine(physicalPath, node.Name + ".xarc");
        if (!File.Exists(xarcPath))
            return;

        long fileSize = new FileInfo(xarcPath).Length;
        TraverseArchive(node, xarcPath, baseOffset: 0, archiveSize: fileSize, node.Name, physicalPath, installRoot, externalModsDir, scanCallback, fileRefs);

        AddLooseFiles(node, physicalPath, installRoot, externalModsDir);
    }

    /// <summary>
    /// Populates <paramref name="node"/> from a <c>.xarc</c> archive that lives at
    /// [<paramref name="baseOffset"/>, <paramref name="baseOffset"/>+<paramref name="archiveSize"/>) within
    /// the outermost archive file at <paramref name="archivePath"/>. Nested <c>.xarc</c> entries recurse
    /// through this same method with an updated base offset, so every leaf entry's stored
    /// <see cref="FsNode.Offset"/> is an ABSOLUTE offset into the outermost file -- which is what
    /// <see cref="OpenFile"/> expects.
    /// </summary>
    private static void TraverseArchive(
        FsNode node,
        string archivePath,
        long baseOffset,
        long archiveSize,
        string archiveName,
        string physicalPath,
        string installRoot,
        string? externalModsDir,
        Action<string>? scanCallback,
        List<XrcFileRef> fileRefs)
    {
        using var archiveStream = new ArchiveWindowStream(archivePath, baseOffset, archiveSize);
        XarcArchive archive = XarcArchive.Read(archiveStream);

        string xrcEntryName = archiveName + ".xrc";
        XarcEntry? xrcEntry = archive.Entries.FirstOrDefault(
            e => e.Name.Equals(xrcEntryName, StringComparison.OrdinalIgnoreCase));

        if (xrcEntry is not null)
        {
            using var window = new ArchiveWindowStream(archivePath, baseOffset + xrcEntry.Offset, xrcEntry.Size);
            XrcStructure structure = XrcStructure.Read(window);

            node.FriendlyName = structure.Name;
            fileRefs.AddRange(structure.Files);

            foreach (XrcLocation location in structure.Locations)
            {
                string? subfolderName = ResolveLocationFolder(physicalPath, location);
                if (subfolderName is null)
                    continue;

                var childNode = new FsNode
                {
                    NodeType = FsNodeType.Directory,
                    Name = subfolderName,
                    Parent = node,
                };
                node.Children.Add(childNode);

                Traverse(childNode, Path.Combine(physicalPath, subfolderName), installRoot, externalModsDir, scanCallback, fileRefs);
            }
        }

        foreach (XarcEntry entry in archive.Entries)
        {
            if (entry.Name.EndsWith(".xarc", StringComparison.OrdinalIgnoreCase))
            {
                // Nested XARCs are containers, not user-facing files. Add a single directory node whose
                // children come from the nested archive's own directory — previously we also added a
                // redundant file leaf beside it, but no workflow ever selected the raw XARC bytes for
                // display or export, so the doubled entry was pure UI noise.
                var nestedNode = new FsNode
                {
                    NodeType = FsNodeType.Directory,
                    Name = Path.GetFileNameWithoutExtension(entry.Name),
                    IsLocalized = entry.IsLocalized,
                    Parent = node,
                };
                node.Children.Add(nestedNode);

                TraverseArchive(
                    nestedNode,
                    archivePath,
                    baseOffset + entry.Offset,
                    entry.Size,
                    Path.GetFileNameWithoutExtension(entry.Name),
                    physicalPath,
                    installRoot,
                    externalModsDir,
                    scanCallback,
                    fileRefs);
                continue;
            }

            var child = new FsNode
            {
                NodeType = FsNodeType.File | FsNodeType.InArchive,
                Name = entry.Name,
                Offset = baseOffset + entry.Offset,
                Size = entry.Size,
                ArchivePath = archivePath,
                IsLocalized = entry.IsLocalized,
                Parent = node,
            };
            node.Children.Add(child);
        }
    }

    private static string? ResolveLocationFolder(string physicalPath, XrcLocation location)
    {
        string byName = Path.Combine(physicalPath, location.Name);
        if (Directory.Exists(byName))
            return location.Name;

        string hexId = location.Id.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
        string byHex = Path.Combine(physicalPath, hexId);
        if (Directory.Exists(byHex))
            return hexId;

        return null;
    }

    /// <summary>
    /// Walks the mod-source folders for this archive — <c>&lt;physicalPath&gt;\xarc\</c> (ScummVM Stark's
    /// in-install convention, <c>engines/stark/services/archiveloader.cpp:83</c>) plus, if configured,
    /// the mirror location <c>&lt;externalModsDir&gt;\&lt;relPath&gt;\xarc\</c> so mods can live outside
    /// the install. External-folder matches take precedence: they override any equivalent in-install mod.
    /// </summary>
    private static void AddLooseFiles(FsNode node, string physicalPath, string installRoot, string? externalModsDir)
    {
        var sources = new List<string>(2);
        string internalDir = Path.Combine(physicalPath, "xarc");
        if (Directory.Exists(internalDir))
            sources.Add(internalDir);

        if (!string.IsNullOrEmpty(externalModsDir))
        {
            string? relativePath = TryGetRelativePath(installRoot, physicalPath);
            if (relativePath is not null)
            {
                string externalDir = Path.Combine(externalModsDir, relativePath, "xarc");
                if (Directory.Exists(externalDir))
                    sources.Add(externalDir);
            }
        }

        if (sources.Count == 0)
            return;

        var existingByName = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase);
        foreach (FsNode child in node.Children)
        {
            if ((child.NodeType & FsNodeType.File) != 0)
                existingByName[child.Name] = child;
        }

        foreach (string looseDir in sources)
        {
            foreach (string filePath in Directory.GetFiles(looseDir))
            {
                string fileName = Path.GetFileName(filePath);
                var info = new FileInfo(filePath);

                FsNode? overridden = ResolveModTarget(existingByName, fileName);
                if (overridden is not null)
                {
                    // Mod override. Later sources (external) win over earlier ones (internal), so external
                    // mods can layer on top of ones a modder has already dropped into the install itself.
                    overridden.ModPath = filePath;
                    continue;
                }

                if (info.Length > MaxLooseFileSize)
                    continue;

                var newNode = new FsNode
                {
                    NodeType = FsNodeType.File,
                    Name = fileName,
                    Size = info.Length,
                    Offset = 0,
                    ArchivePath = filePath,
                    Parent = node,
                };
                node.Children.Add(newNode);
                existingByName[fileName] = newNode;
            }
        }
    }

    /// <summary>
    /// Locates the archive entry a loose mod file overrides. Handles both the trivial case (same name
    /// as an archive entry) and Stark's extension-swap conventions: a <c>.png</c> mod file overrides the
    /// <c>.xmg</c> archive entry with the same stem (see <c>engines/stark/resources/image.cpp</c>
    /// <c>loadPNGOverride</c>), and the container-passthrough swaps <c>.bik→.bbb</c>, <c>.smk→.sss</c>,
    /// <c>.ogg→.ovs</c> (see <see cref="Formats.ContainerUnwrap"/>) so mods can ship the well-known
    /// media file directly instead of the TLJ-renamed archive extension.
    /// </summary>
    private static FsNode? ResolveModTarget(Dictionary<string, FsNode> existingByName, string looseFileName)
    {
        if (existingByName.TryGetValue(looseFileName, out FsNode? exact))
            return exact;

        foreach (string swapped in SwapModToArchiveCandidates(looseFileName))
        {
            if (existingByName.TryGetValue(swapped, out FsNode? viaSwap))
                return viaSwap;
        }

        return null;
    }

    /// <summary>
    /// Enumerates archive-entry names a loose mod file could override, in priority order. Videos are
    /// tried under both container extensions (<c>.bbb</c> Bink / <c>.sss</c> Smacker) so mods that swap
    /// codec — e.g. TLJHD replacing Smacker cutscenes with Bink — still attach.
    /// </summary>
    private static IEnumerable<string> SwapModToArchiveCandidates(string modFileName)
    {
        string ext = Path.GetExtension(modFileName);
        string[] archiveExts = ext.ToLowerInvariant() switch
        {
            ".png" => [".xmg"],
            ".bik" => [".bbb", ".sss"],
            ".smk" => [".sss", ".bbb"],
            ".ogg" => [".ovs"],
            _ => [],
        };

        foreach (string archiveExt in archiveExts)
            yield return Path.ChangeExtension(modFileName, archiveExt);
    }

    /// <summary>
    /// Inverse of <see cref="SwapModToArchiveCandidates"/>: given an archive-entry name, returns the
    /// mod-file names that would override it. Videos accept either container extension.
    /// </summary>
    private static IEnumerable<string> SwapArchiveToModCandidates(string archiveFileName)
    {
        string ext = Path.GetExtension(archiveFileName);
        string[] modExts = ext.ToLowerInvariant() switch
        {
            ".xmg" => [".png"],
            ".bbb" => [".bik", ".smk"],
            ".sss" => [".smk", ".bik"],
            ".ovs" => [".ogg"],
            _ => [],
        };

        foreach (string modExt in modExts)
            yield return Path.ChangeExtension(archiveFileName, modExt);
    }

    /// <summary>
    /// Returns <paramref name="physicalPath"/> relative to <paramref name="installRoot"/>, or null when
    /// <paramref name="physicalPath"/> escapes the install root (rare — would only happen if the caller
    /// hands us paths that don't share a common ancestor). Case-insensitive on Windows conventions.
    /// </summary>
    private static string? TryGetRelativePath(string installRoot, string physicalPath)
    {
        string install = Path.GetFullPath(installRoot).TrimEnd('\\', '/');
        string sub = Path.GetFullPath(physicalPath).TrimEnd('\\', '/');
        if (sub.Equals(install, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string withSeparator = install + Path.DirectorySeparatorChar;
        if (!sub.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
            return null;

        return sub.Substring(withSeparator.Length);
    }

    /// <summary>
    /// Attaches friendly names/extended info collected from every <c>.xrc</c> read during traversal to the
    /// matching file nodes, anywhere in the tree. Matching is case-insensitive and also tries swapping the
    /// <c>.isn</c>/<c>.ovs</c> extension in either direction, since sounds are referenced by their
    /// pre-OVS-wrap name.
    /// </summary>
    private static void ApplyFileRefs(FsNode root, List<XrcFileRef> fileRefs)
    {
        if (fileRefs.Count == 0)
            return;

        var byName = new Dictionary<string, List<FsNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (FsNode fileNode in EnumerateFiles(root))
        {
            AddToIndex(byName, fileNode.Name, fileNode);

            string swapped = SwapIsnOvsExtension(fileNode.Name);
            if (!swapped.Equals(fileNode.Name, StringComparison.OrdinalIgnoreCase))
                AddToIndex(byName, swapped, fileNode);
        }

        foreach (XrcFileRef fileRef in fileRefs)
        {
            if (!byName.TryGetValue(fileRef.Name, out List<FsNode>? matches))
                continue;

            foreach (FsNode match in matches)
            {
                // XrcStructureReader emits a dialogue-wrapped sound twice: once with subtitles (from the
                // TypeDialogue branch) and once bare (from the recursive walk into the Sound child).
                // Order isn't guaranteed, so keep whichever variant carries actual data rather than let
                // the later ref clobber the earlier with nulls.
                if (fileRef.ExtendedInfo is { Length: > 0 })
                    match.ExtendedInfo = fileRef.ExtendedInfo;
                if (string.IsNullOrEmpty(match.FriendlyName) && !string.IsNullOrEmpty(fileRef.FriendlyName))
                    match.FriendlyName = fileRef.FriendlyName;
            }
        }
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

    private static string SwapIsnOvsExtension(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext.Equals(".isn", StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(name, ".ovs");
        if (ext.Equals(".ovs", StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(name, ".isn");
        return name;
    }

    private static IEnumerable<FsNode> EnumerateFiles(FsNode node)
    {
        foreach (FsNode child in node.Children)
        {
            if ((child.NodeType & FsNodeType.File) != 0)
                yield return child;

            if ((child.NodeType & FsNodeType.Directory) != 0)
            {
                foreach (FsNode descendant in EnumerateFiles(child))
                    yield return descendant;
            }
        }
    }

    /// <summary>
    /// Grafts the "static" folders (diary/menu/etc. screens) under the root, if present on disk. These are
    /// never archive-backed: every file directly inside each is added as a plain loose <see cref="FsNode"/>.
    /// </summary>
    private static void GraftStaticFolders(FsNode root, string baseDir)
    {
        string staticDir = Path.Combine(baseDir, "static");

        var staticNode = new FsNode
        {
            NodeType = FsNodeType.Directory,
            Name = "static",
            Parent = root,
        };
        root.Children.Add(staticNode);

        foreach (string subfolder in StaticSubfolders)
        {
            string subfolderPath = Path.Combine(staticDir, subfolder);
            if (!Directory.Exists(subfolderPath))
                continue;

            var folderNode = new FsNode
            {
                NodeType = FsNodeType.Directory,
                Name = subfolder,
                Parent = staticNode,
            };
            staticNode.Children.Add(folderNode);

            foreach (string filePath in Directory.GetFiles(subfolderPath))
            {
                var info = new FileInfo(filePath);
                folderNode.Children.Add(new FsNode
                {
                    NodeType = FsNodeType.File,
                    Name = info.Name,
                    Size = info.Length,
                    ArchivePath = filePath,
                    Parent = folderNode,
                });
            }
        }
    }

    /// <summary>
    /// Resolves a <c>\</c>-delimited path against <paramref name="from"/> (or against <see cref="Root"/> if
    /// the path starts with <c>\</c>, i.e. is absolute). Supports <c>.</c> and <c>..</c> segments;
    /// child-name matching is case-insensitive.
    /// </summary>
    public FsNode? FindNode(FsNode from, string path)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(path);

        FsNode current = path.StartsWith('\\') || path.StartsWith('/') ? Root : from;

        string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                current = current.Parent ?? current;
                continue;
            }

            FsNode? next = current.Children.Find(
                c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

            if (next is null)
                return null;

            current = next;
        }

        return current;
    }

    public IEnumerable<FsNode> GetDirectories(FsNode dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        return dir.Children
            .Where(c => (c.NodeType & FsNodeType.Directory) != 0)
            .OrderBy(c => c, DirectoryComparer.Instance);
    }

    public IEnumerable<FsNode> GetFiles(FsNode dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        return dir.Children
            .Where(c => (c.NodeType & FsNodeType.File) != 0)
            .OrderBy(c => c, FileComparer.Instance);
    }

    /// <summary>
    /// Opens a read stream over <paramref name="file"/>'s bytes: a window into its owning <c>.xarc</c> for
    /// archive-backed entries, or the whole physical file otherwise. When <see cref="LoadMods"/> is on
    /// and the entry has a <see cref="FsNode.ModPath"/>, the mod file is returned instead.
    /// </summary>
    public Stream OpenFile(FsNode file) => OpenFile(file, LoadMods ? OpenVariant.Preferred : OpenVariant.Original);

    /// <summary>Explicit-variant open used by the compare view: force original or force mod.</summary>
    public Stream OpenFile(FsNode file, OpenVariant variant)
    {
        ArgumentNullException.ThrowIfNull(file);

        bool useMod = variant switch
        {
            OpenVariant.Mod => file.HasMod,
            OpenVariant.Preferred => file.HasMod && LoadMods,
            _ => false,
        };

        if (useMod)
            return new FileStream(file.ModPath!, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (string.IsNullOrEmpty(file.ArchivePath))
            throw new InvalidOperationException($"File node '{file.GetPath()}' has no known physical location.");

        return (file.NodeType & FsNodeType.InArchive) != 0
            ? new ArchiveWindowStream(file.ArchivePath, file.Offset, file.Size)
            : new ArchiveWindowStream(file.ArchivePath, 0, file.Size);
    }

    /// <summary>Which variant of a possibly-modded file to open.</summary>
    public enum OpenVariant
    {
        /// <summary>Follow <see cref="LoadMods"/>: mod when the setting is on and one exists, original otherwise.</summary>
        Preferred,
        /// <summary>Force the archive/original variant, even if the entry has a mod.</summary>
        Original,
        /// <summary>Force the mod variant. Throws if the entry has no <see cref="FsNode.ModPath"/>.</summary>
        Mod,
    }

    /// <summary>True if <paramref name="name"/> looks like a hex byte folder name (1-2 hex digits, e.g. "a" or "3f").</summary>
    private static bool IsHexByte(string name) =>
        name.Length is 1 or 2 && name.All(Uri.IsHexDigit);

    private sealed class DirectoryComparer : IComparer<FsNode>
    {
        public static readonly DirectoryComparer Instance = new();

        public int Compare(FsNode? x, FsNode? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            bool xHex = IsHexByte(x.Name);
            bool yHex = IsHexByte(y.Name);

            if (xHex != yHex)
                return xHex ? 1 : -1;

            return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FileComparer : IComparer<FsNode>
    {
        public static readonly FileComparer Instance = new();

        public int Compare(FsNode? x, FsNode? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            int extCompare = string.Compare(
                Path.GetExtension(x.Name), Path.GetExtension(y.Name), StringComparison.OrdinalIgnoreCase);
            if (extCompare != 0)
                return extCompare;

            return string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
