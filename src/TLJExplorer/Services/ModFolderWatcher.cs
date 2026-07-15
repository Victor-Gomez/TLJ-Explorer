using System.IO;
using Avalonia.Threading;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Services;

/// <summary>Fired when a mod file appears, changes, or disappears under one of the watched <c>xarc/</c> folders.</summary>
/// <param name="Node">The affected VFS node; <see cref="FsNode.ModPath"/> has been updated already.</param>
/// <param name="Kind">What happened to the mod file.</param>
public sealed record ModChangeNotification(FsNode Node, ModChangeKind Kind);

public enum ModChangeKind { Added, Updated, Removed }

/// <summary>
/// Watches every <c>xarc/</c> subfolder found under the loaded install and updates the corresponding
/// <see cref="FsNode.ModPath"/> when mod files are added, changed, or removed at runtime. The idea is to
/// let modders drop a file into place (or edit it in their tool of choice) and see the change reflect in
/// TLJ Explorer without reloading the install.
/// </summary>
/// <remarks>
/// One <see cref="FileSystemWatcher"/> per <c>xarc/</c> directory (there are typically only a few dozen).
/// Change events are marshaled onto <see cref="Dispatcher"/> before the callback runs, so consumers can
/// safely touch UI state.
/// </remarks>
public sealed class ModFolderWatcher : IDisposable
{
    private readonly VirtualFileSystem _vfs;
    private readonly Dispatcher _dispatcher;
    private readonly Action<ModChangeNotification> _onChange;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, FsNode> _byAbsolutePath = new(StringComparer.OrdinalIgnoreCase);

    public ModFolderWatcher(VirtualFileSystem vfs, Dispatcher dispatcher, Action<ModChangeNotification> onChange)
    {
        _vfs = vfs;
        _dispatcher = dispatcher;
        _onChange = onChange;

        IndexExistingMods(_vfs.Root);
        AttachWatchers();
    }

    /// <summary>
    /// Walks the VFS once and records every archived file's expected mod path so change notifications
    /// can look up the target <see cref="FsNode"/> in O(1). We also record the parent xarc/ directory so
    /// nodes that don't yet have a mod file get one attached the moment a matching file appears.
    /// </summary>
    private void IndexExistingMods(FsNode node)
    {
        foreach (FsNode child in node.Children)
        {
            if ((child.NodeType & FsNodeType.File) != 0 && (child.NodeType & FsNodeType.InArchive) != 0 &&
                !string.IsNullOrEmpty(child.ArchivePath))
            {
                string? archiveDir = Path.GetDirectoryName(child.ArchivePath);
                if (!string.IsNullOrEmpty(archiveDir))
                {
                    string modDir = Path.Combine(archiveDir, "xarc");

                    // Same-name override.
                    _byAbsolutePath.TryAdd(Path.Combine(modDir, child.Name), child);

                    // Stark's PNG-for-XMG rule: file at <modDir>/<stem>.png overrides <stem>.xmg.
                    if (child.Name.EndsWith(".xmg", StringComparison.OrdinalIgnoreCase))
                    {
                        string swappedName = Path.ChangeExtension(child.Name, ".png");
                        _byAbsolutePath.TryAdd(Path.Combine(modDir, swappedName), child);
                    }
                }
            }

            if ((child.NodeType & FsNodeType.Directory) != 0)
                IndexExistingMods(child);
        }
    }

    private void AttachWatchers()
    {
        // Distinct xarc/ directories: each may contain multiple mod files for the archive next to it.
        var xarcDirs = _byAbsolutePath.Keys
            .Select(p => Path.GetDirectoryName(p))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string? dir in xarcDirs)
        {
            if (dir is null || !Directory.Exists(dir))
                continue;
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                w.Created += (_, e) => OnFsEvent(e.FullPath, ModChangeKind.Added);
                w.Changed += (_, e) => OnFsEvent(e.FullPath, ModChangeKind.Updated);
                w.Deleted += (_, e) => OnFsEvent(e.FullPath, ModChangeKind.Removed);
                w.Renamed += (_, e) =>
                {
                    OnFsEvent(e.OldFullPath, ModChangeKind.Removed);
                    OnFsEvent(e.FullPath, ModChangeKind.Added);
                };
                _watchers.Add(w);
            }
            catch
            {
                // Some directories (network shares, permission-limited paths) can't be watched. Silently
                // skip them — losing live-reload for a subset of archives is not fatal.
            }
        }
    }

    private void OnFsEvent(string path, ModChangeKind kind)
    {
        if (!_byAbsolutePath.TryGetValue(path, out FsNode? node))
            return;

        // FileSystemWatcher fires many "Changed" events during a single file save (Windows often reports
        // 2-3 in a row as editors rewrite the file). Update the FsNode either way — the value is
        // idempotent — but marshal to the UI thread once per event.
        _dispatcher.Post(() =>
        {
            node.ModPath = kind == ModChangeKind.Removed ? null : path;
            _onChange(new ModChangeNotification(node, kind));
        });
    }

    public void Dispose()
    {
        foreach (FileSystemWatcher w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch { /* best effort on shutdown */ }
        }
        _watchers.Clear();
    }
}
