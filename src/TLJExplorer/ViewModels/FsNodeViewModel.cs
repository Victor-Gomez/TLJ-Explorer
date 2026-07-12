using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.ViewModels;

/// <summary>
/// Lazily-populated TreeView wrapper around an <see cref="FsNode"/>. The underlying <see cref="FsNode"/>
/// tree is already fully built in memory by <see cref="VirtualFileSystem.Init"/>, but a TLJ install can
/// contain tens of thousands of nodes -- eagerly materializing a view-model + TreeViewItem for every one
/// of them up front would be slow and memory-hungry. Instead, each node gets a single placeholder child
/// until it is actually expanded, at which point real children are materialized on demand.
/// </summary>
public sealed class FsNodeViewModel : INotifyPropertyChanged
{
    private const string IconBasePath = "pack://application:,,,/Assets/Icons/";

    // Vector icons (Assets/Icons/VectorIcons.xaml, merged into Application.Resources) take priority
    // over the raster .ico set for the categories they cover.
    private static readonly Dictionary<string, string> ExtensionVectorIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ani"] = "AnimationTypeIcon",
        [".bbb"] = "VideoTypeIcon",
        [".biff"] = "BiffTypeIcon",
        [".cir"] = "ModelTypeIcon",
        [".isn"] = "SoundTypeIcon",
        [".iss"] = "SoundTypeIcon",
        [".ovs"] = "SoundTypeIcon",
        [".sn"] = "SoundTypeIcon",
        [".ssn"] = "SoundTypeIcon",
        [".sss"] = "VideoTypeIcon",
        [".tm"] = "ImageTypeIcon",
        [".xmg"] = "ImageTypeIcon",
        [".xarc"] = "ArchiveTypeIcon",
        [".xrc"] = "DataTypeIcon",
    };

    private const string RawFileVectorIcon = "RawFileTypeIcon";
    private const string RootVectorIcon = "HouseIcon";
    private const string FolderClosedVectorIcon = "FolderClosedTypeIcon";
    private const string FolderOpenVectorIcon = "FolderOpenTypeIcon";

    // Icons are requested once per visible row, but a tree can have tens of thousands of rows -- cache
    // the resolved ImageSource per icon key instead of re-decoding/re-looking-up on every binding pull.
    private static readonly Dictionary<string, ImageSource> IconCache = [];

    private readonly VirtualFileSystem? _vfs;
    private bool _childrenLoaded;
    private bool _isExpanded;

    public FsNode Node { get; }

    /// <summary>True for the single synthetic "loading" placeholder inserted so an unexpanded node still shows an expander arrow.</summary>
    public bool IsPlaceholder { get; }

    public ObservableCollection<FsNodeViewModel> Children { get; } = [];

    /// <summary>
    /// When the tree filter matched inside a subtitle line (not the file name), the specific matched line
    /// is stored here so the row template can show it as a small secondary line. <c>null</c> for name-only
    /// matches or when no filter is active.
    /// </summary>
    public string? SubtitleMatch { get; set; }

    public bool HasSubtitleMatch => !string.IsNullOrEmpty(SubtitleMatch);

    /// <summary>Convenience proxy for the tree data template.</summary>
    public bool HasMod => Node.HasMod;

    /// <summary>
    /// Kick the tree template to re-read <see cref="HasMod"/>. Called after Extract-as-Mod or the file
    /// watcher mutates <see cref="FsNode.ModPath"/> so the "MOD" pill appears/disappears without a full
    /// tree rebuild.
    /// </summary>
    public void RefreshModIndicator() => OnPropertyChanged(nameof(HasMod));

    public FsNodeViewModel(FsNode node, VirtualFileSystem vfs)
        : this(node, vfs, isPlaceholder: false, eagerChildren: null)
    {
    }

    private FsNodeViewModel(FsNode node, VirtualFileSystem? vfs, bool isPlaceholder, List<FsNodeViewModel>? eagerChildren)
    {
        Node = node;
        _vfs = vfs;
        IsPlaceholder = isPlaceholder;

        if (eagerChildren is not null)
        {
            _childrenLoaded = true;
            foreach (FsNodeViewModel child in eagerChildren)
                Children.Add(child);

            // Deliberately NOT auto-expanded: with a broad type filter (e.g. "Images") almost every
            // folder can contain a match, so forcing every matching directory open effectively expands
            // the whole tree. Only the filtered root gets auto-expanded (by the caller), same as the
            // normal unfiltered tree -- the user expands folders manually from there.
        }
        else if (!isPlaceholder && node.Children.Count > 0)
        {
            Children.Add(new FsNodeViewModel(node, vfs: null, isPlaceholder: true, eagerChildren: null));
        }
    }

    /// <summary>
    /// Eagerly builds a pruned view of the tree rooted at <paramref name="node"/> containing only file
    /// nodes matching <paramref name="matchesFile"/> and the directories needed to reach them. Returns
    /// <c>null</c> if nothing under <paramref name="node"/> matches (including <paramref name="node"/>
    /// itself, if it's a file). Unlike the lazy constructor, this walks the whole subtree up front --
    /// used for the type filter / search box, where we need to know in advance which branches contain
    /// a match at all.
    /// </summary>
    public static FsNodeViewModel? BuildFiltered(
        FsNode node,
        VirtualFileSystem vfs,
        Func<FsNode, bool> matchesFile,
        Func<FsNode, string?>? extractSubtitleMatch = null)
    {
        if ((node.NodeType & FsNodeType.File) != 0)
        {
            if (!matchesFile(node))
                return null;
            var vm = new FsNodeViewModel(node, vfs, isPlaceholder: false, eagerChildren: []);
            vm.SubtitleMatch = extractSubtitleMatch?.Invoke(node);
            return vm;
        }

        var matchingChildren = new List<FsNodeViewModel>();

        foreach (FsNode dir in vfs.GetDirectories(node))
        {
            FsNodeViewModel? childVm = BuildFiltered(dir, vfs, matchesFile, extractSubtitleMatch);
            if (childVm is not null)
                matchingChildren.Add(childVm);
        }

        foreach (FsNode file in vfs.GetFiles(node))
        {
            FsNodeViewModel? childVm = BuildFiltered(file, vfs, matchesFile, extractSubtitleMatch);
            if (childVm is not null)
                matchingChildren.Add(childVm);
        }

        if (matchingChildren.Count == 0)
            return null;

        return new FsNodeViewModel(node, vfs, isPlaceholder: false, eagerChildren: matchingChildren);
    }

    public bool IsDirectory => (Node.NodeType & FsNodeType.Directory) != 0;

    public bool IsFile => (Node.NodeType & FsNodeType.File) != 0;

    public string DisplayName => IsPlaceholder ? "Loading..." : Node.DisplayName;

    /// <summary>
    /// This node's icon: a vector <see cref="DrawingImage"/> (see <c>Assets/Icons/VectorIcons.xaml</c>)
    /// for the categories that have one (image/sound/video/3D-model/animation/open-closed folder),
    /// otherwise a raster <c>.ico</c> from the per-file-type icon set (root / remaining resource
    /// extensions / generic raw-file icon for anything unrecognized).
    /// </summary>
    public ImageSource? IconSource
    {
        get
        {
            if (IsPlaceholder)
                return null;

            if (IsDirectory)
            {
                if ((Node.NodeType & FsNodeType.Root) != 0)
                    return GetVectorIcon(RootVectorIcon);

                return GetVectorIcon(IsExpanded ? FolderOpenVectorIcon : FolderClosedVectorIcon);
            }

            string ext = Path.GetExtension(Node.Name);
            if (ExtensionVectorIcons.TryGetValue(ext, out string? vectorKey))
                return GetVectorIcon(vectorKey);

            return GetVectorIcon(RawFileVectorIcon);
        }
    }

    private static ImageSource GetRasterIcon(string fileName)
    {
        string cacheKey = "raster:" + fileName;
        if (IconCache.TryGetValue(cacheKey, out ImageSource? cached))
            return cached;

        var image = new BitmapImage(new Uri(IconBasePath + fileName));
        image.Freeze();
        IconCache[cacheKey] = image;
        return image;
    }

    private static ImageSource GetVectorIcon(string resourceKey)
    {
        string cacheKey = "vector:" + resourceKey;
        if (IconCache.TryGetValue(cacheKey, out ImageSource? cached))
            return cached;

        var image = (ImageSource)Application.Current.Resources[resourceKey];
        IconCache[cacheKey] = image;
        return image;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;

            _isExpanded = value;

            if (value && !_childrenLoaded)
                LoadChildren();

            OnPropertyChanged();
            if (IsDirectory)
                OnPropertyChanged(nameof(IconSource));
        }
    }

    private void LoadChildren()
    {
        _childrenLoaded = true;
        Children.Clear();

        if (_vfs is null)
            return;

        foreach (FsNode dir in _vfs.GetDirectories(Node))
            Children.Add(new FsNodeViewModel(dir, _vfs));

        foreach (FsNode file in _vfs.GetFiles(Node))
            Children.Add(new FsNodeViewModel(file, _vfs));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
