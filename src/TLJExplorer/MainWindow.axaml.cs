using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TLJExplorer.Services;
using TLJExplorer.ViewModels;
using TLJExplorer.Views;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Core.Settings;

namespace TLJExplorer;

/// <summary>
/// Interaction logic for MainWindow.axaml. Owns the loaded <see cref="VirtualFileSystem"/>, the currently
/// selected resource, and the sound player. Deliberately a thin code-behind rather than MVVM.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TempFileTracker _tempFiles;
    // Lazily created on first actual use (opening a sound/video resource) rather than field initializers:
    // constructing a LibVlcMediaPlayer spins up the native libvlc engine (plugin discovery etc.), which
    // takes a couple hundred ms -- eating that eagerly here would delay showing the main window.
    private LibVlcMediaPlayer? _mediaPlayerBacking;
    private LibVlcMediaPlayer _mediaPlayer => _mediaPlayerBacking ??= CreateSoundPlayer();

    private LibVlcMediaPlayer? _videoPlayerBacking;
    private LibVlcMediaPlayer _videoPlayer => _videoPlayerBacking ??= CreateVideoPlayer();

    private LibVlcMediaPlayer CreateSoundPlayer()
    {
        var player = new LibVlcMediaPlayer();
        player.MediaOpened += MediaPlayer_MediaOpened;
        player.MediaEnded += MediaPlayer_MediaEnded_SoundLoop;
        return player;
    }

    private LibVlcMediaPlayer CreateVideoPlayer()
    {
        var player = new LibVlcMediaPlayer();
        player.MediaEnded += VideoPlayer_MediaEnded;
        VideoPlayer.MediaPlayer = player.NativePlayer;
        return player;
    }

    private readonly DispatcherTimer _positionTimer;

    private readonly DispatcherTimer _searchDebounceTimer;

    // Avalonia has no Window.IsInitialized; several ValueChanged handlers fire while InitializeComponent
    // is still wiring up named fields (sliders raise ValueChanged as soon as their XAML-declared Value is
    // applied). This flips true at the end of the constructor, same guard purpose as WPF's IsInitialized.
    private bool _initialized;

    // Avalonia doesn't generate x:Name fields for objects assigned via a property (LayoutTransform, Clip)
    // rather than being a visual-tree child -- by design, see AvaloniaUI/Avalonia#20269. These are built
    // here instead and wired onto their host control's property in the constructor.
    private readonly ScaleTransform _imageScale = new();
    private readonly ScaleTransform _imageCompareStageScale = new();
    private readonly ScaleTransform _imageCompareSideOriginalScale = new();
    private readonly ScaleTransform _imageCompareSideModScale = new();
    private readonly ScaleTransform _sceneScale = new();
    private readonly ScaleTransform _videoScale = new();
    private readonly RectangleGeometry _imageCompareOriginalClip = new();
    private readonly RectangleGeometry _imageCompareModClip = new();

    private CancellationTokenSource? _batchExportCts;
    private ModFolderWatcher? _modFolderWatcher;
    private float[]? _cachedWaveformPeaks;
    private SoundResource? _cachedWaveformSource;

    private VirtualFileSystem? _vfs;
    private FsNode? _selectedNode;
    private ResourceContent? _currentContent;
    private bool _sliderDragging;

    // Incremented on every new resource selection; background texture loads compare their captured value
    // against this and drop stale results (user clicked away while the load was in flight).
    private int _modelLoadGeneration;

    // Same idea for the async resource-decode path: LoadSelectedResource hops the decode onto a worker
    // thread, and the continuation checks this counter to avoid a slow-loading asset overwriting a newer
    // selection when it finally lands.
    private int _resourceLoadGeneration;

    // ---------------------------------------------------------------------
    // Model / Skin / Animation browsing (see ModelBrowseCatalog)
    // ---------------------------------------------------------------------

    /// <summary>Whole-VFS catalog of every .cir/.ani/.tm file, built once per VFS load (see InitVfsAsync) and reused across every model selection.</summary>
    private ModelBrowseCatalog? _modelCatalog;

    private CirModel? _currentModel;
    private FsNode? _currentModelNode;
    private FsNode? _currentAnimationNode;
    private bool _modelSliderDragging;
    private bool _updatingModelCombos;

    private readonly DispatcherTimer _modelPlaybackTimer;

    // ---------------------------------------------------------------------
    // Scene rendering state
    // ---------------------------------------------------------------------

    private int _sceneLoadGeneration;
    private double _sceneZoom = 1.0;
    private DispatcherTimer? _sceneFrameTimer;

    /// <summary>Per-overlay animation state: the Image control on the canvas, its frame sequence, and the mutable index into it.</summary>
    private sealed class SceneOverlayInstance(Image imageControl, IReadOnlyList<Bitmap> frames)
    {
        public Image ImageControl { get; } = imageControl;
        public IReadOnlyList<Bitmap> Frames { get; } = frames;
        public int FrameIndex { get; set; }
    }

    private readonly List<SceneOverlayInstance> _sceneOverlays = [];

    /// <summary>A single entry in the Model/Skin/Animation dropdowns. <see cref="Node"/> is <see langword="null"/> for the synthetic "(none)" entry offered by the Skin/Animation combos.</summary>
    private sealed record ModelPickItem(string DisplayName, FsNode? Node)
    {
        public override string ToString() => DisplayName;
    }

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _tempFiles = ((App)Application.Current!).TempFiles;

        // See the _imageScale/etc. field comments: these aren't x:Name-addressable from XAML, so wire
        // them onto their host controls' LayoutTransform/Clip properties here instead.
        PreviewImageTransformHost.LayoutTransform = _imageScale;
        ImageCompareStageTransformHost.LayoutTransform = _imageCompareStageScale;
        ImageCompareSideOriginalTransformHost.LayoutTransform = _imageCompareSideOriginalScale;
        ImageCompareSideModTransformHost.LayoutTransform = _imageCompareSideModScale;
        SceneTransformHost.LayoutTransform = _sceneScale;
        VideoTransformHost.LayoutTransform = _videoScale;
        ImageCompareOriginalImage.Clip = _imageCompareOriginalClip;
        ImageCompareModImage.Clip = _imageCompareModClip;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += PositionTimer_Tick;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _ = ApplyTreeFilterAsync();
        };

        _modelPlaybackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _modelPlaybackTimer.Tick += ModelPlaybackTimer_Tick;
        _modelPlaybackTimer.Start();

        TypeFilterCombo.ItemsSource = ResourceTypeFilter.Categories;
        TypeFilterCombo.SelectedIndex = 0;

        InitializeOptionsMenu();

        // Tunnel routing so shortcuts fire even when a child control has keyboard focus, mirroring WPF's
        // PreviewKeyDown. Wired via AddHandler (not a XAML attribute) since Avalonia's XAML event syntax
        // always attaches at the default (bubble) routing strategy.
        AddHandler(KeyDownEvent, MainWindow_PreviewKeyDown, RoutingStrategies.Tunnel);

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            // Guard on the backing field, not the lazy property: if the user never played a sound/video
            // this session, don't force the (expensive) native player to spin up just to tear it down.
            _mediaPlayerBacking?.Dispose();
            if (_videoPlayerBacking is not null)
            {
                StopVideo();
                _videoPlayerBacking.Dispose();
            }
            ModelViewerHost.Dispose();
            _modFolderWatcher?.Dispose();
        };

        // Wheel handlers need Tunnel routing to intercept before the ScrollViewer's own native scroll
        // response (mirrors WPF's PreviewMouseWheel usage for the same purpose).
        ImageScrollViewer.AddHandler(PointerWheelChangedEvent, ImageScrollViewer_PreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        ImageCompareWipeScroll.AddHandler(PointerWheelChangedEvent, ImageCompare_PreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        ImageCompareSideOriginalScroll.AddHandler(PointerWheelChangedEvent, ImageCompare_PreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        ImageCompareSideModScroll.AddHandler(PointerWheelChangedEvent, ImageCompare_PreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        SceneScrollViewer.AddHandler(PointerWheelChangedEvent, SceneScrollViewer_PreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        VideoScrollViewer.AddHandler(PointerWheelChangedEvent, VideoScrollViewer_PreviewPointerWheelChanged, RoutingStrategies.Tunnel);

        // Pan-drag handlers for the image and compare viewers (bubble routing is fine here -- there's no
        // default ScrollViewer behavior to defeat, unlike wheel-zoom).
        ImageScrollViewer.PointerPressed += ImageScrollViewer_PointerPressed;
        ImageScrollViewer.PointerMoved += ImageScrollViewer_PointerMoved;
        ImageScrollViewer.PointerReleased += ImageScrollViewer_PointerReleased;
        ImageCompareWipeScroll.PointerPressed += ImageComparePanScroll_PointerPressed;
        ImageCompareWipeScroll.PointerMoved += ImageComparePanScroll_PointerMoved;
        ImageCompareWipeScroll.PointerReleased += ImageComparePanScroll_PointerReleased;
        ImageCompareSideOriginalScroll.PointerPressed += ImageComparePanScroll_PointerPressed;
        ImageCompareSideOriginalScroll.PointerMoved += ImageComparePanScroll_PointerMoved;
        ImageCompareSideOriginalScroll.PointerReleased += ImageComparePanScroll_PointerReleased;
        ImageCompareSideModScroll.PointerPressed += ImageComparePanScroll_PointerPressed;
        ImageCompareSideModScroll.PointerMoved += ImageComparePanScroll_PointerMoved;
        ImageCompareSideModScroll.PointerReleased += ImageComparePanScroll_PointerReleased;

        _initialized = true;
    }

    /// <summary>
    /// App-wide keyboard shortcuts. Deliberately tunnel-routed so they fire even when the tree has
    /// keyboard focus, but we bail out when the user is typing in the search box -- otherwise "F" would
    /// silently steal the letter, and Space would trigger playback instead of inserting a space.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        bool typingInSearchBox = SearchBox.IsFocused;

        // Escape dismisses the in-app settings overlay when it's open. Handled before other
        // shortcuts so it always wins while settings are showing.
        if (e.Key == Key.Escape && SettingsOverlay.IsVisible)
        {
            SettingsOverlay.Hide();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.O)
        {
            SelectFolder_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.E)
        {
            Export_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.P)
        {
            OpenCommandPalette();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            OpenSettings_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!typingInSearchBox && e.Key == Key.Space)
        {
            // Context-aware: whichever panel is visible gets the space bar.
            if (ModelPanel.IsVisible && ModelViewerHost.HasAnimation)
            {
                if (ModelViewerHost.IsPlaying) ModelViewerHost.Pause();
                else ModelViewerHost.Play();
                e.Handled = true;
            }
            else if (SoundPanel.IsVisible)
            {
                PlayPauseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if (!typingInSearchBox && SoundPanel.IsVisible &&
                 (e.Key == Key.Left || e.Key == Key.Right))
        {
            // Arrow-key seek for the sound player: +-5 seconds, clamped to the file's duration.
            if (_mediaPlayer.HasDurationTimeSpan)
            {
                TimeSpan delta = TimeSpan.FromSeconds(e.Key == Key.Right ? 5 : -5);
                TimeSpan target = _mediaPlayer.Position + delta;
                if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                if (_mediaPlayer.Duration is { } dur && target > dur) target = dur;
                _mediaPlayer.Position = target;
                SoundSlider.Value = target.TotalSeconds;
                UpdateSoundTimeReadout();
                e.Handled = true;
            }
        }
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.BaseDir))
        {
            await InitVfsAsync(_settings.BaseDir);
        }
        else
        {
            SetStatus("No TLJ install selected. Use File -> Select TLJ Install Folder...");
        }
    }

    // ---------------------------------------------------------------------
    // VFS loading
    // ---------------------------------------------------------------------

    private async Task InitVfsAsync(string baseDir)
    {
        ScanProgressBar.IsVisible = true;
        ScanProgressBar.IsIndeterminate = true;
        SetStatus($"Scanning \"{baseDir}\"...");

        TypeFilterCombo.SelectedIndex = 0;
        SearchBox.Text = string.Empty;

        try
        {
            string? externalMods = _settings.ExternalModsDir;
            if (!string.IsNullOrEmpty(externalMods) && !Directory.Exists(externalMods))
                externalMods = null;

            VirtualFileSystem vfs = await Task.Run(() => VirtualFileSystem.Init(
                baseDir,
                path => Dispatcher.UIThread.Post(() => SetStatus($"Scanning: {path}")),
                externalMods));

            _vfs = vfs;
            _vfs.LoadMods = _settings.LoadAssetMods;
            _modelCatalog = null;

            // Tear down any previously-attached watcher (e.g. switching installs) and start a fresh one
            // over this VFS's xarc/ folders.
            _modFolderWatcher?.Dispose();
            _modFolderWatcher = new ModFolderWatcher(vfs, Dispatcher.UIThread, OnModChanged);

            var rootVm = new FsNodeViewModel(vfs.Root, vfs) { IsExpanded = true };
            Tree.ItemsSource = new[] { rootVm };

            SetStatus($"Loaded \"{baseDir}\".");

            // Restore last selection for this install, if any. Deferred so the tree has been rendered by
            // Avalonia before we try to walk to and select a specific node.
            if (_settings.LastSelectedPath.TryGetValue(baseDir, out string? savedPath) &&
                !string.IsNullOrEmpty(savedPath))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    FsNode? target = vfs.FindNode(vfs.Root, savedPath);
                    if (target is not null)
                        TryRestoreTreeSelection(rootVm, target);
                }, DispatcherPriority.Background);
            }

            // Build the whole-install Model/Skin/Animation catalog once, off the UI thread, so the
            // "Include whole install" checkbox has something to show without re-walking the tree.
            _ = Task.Run(() => ModelBrowseCatalog.Build(vfs)).ContinueWith(
                t =>
                {
                    if (!t.IsFaulted && ReferenceEquals(_vfs, vfs))
                        _modelCatalog = t.Result;
                },
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(
                this,
                $"Could not load a TLJ install from:\n{baseDir}\n\n{ex.Message}",
                "TLJ Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetStatus("No TLJ install loaded.");
        }
        finally
        {
            ScanProgressBar.IsVisible = false;
            ScanProgressBar.IsIndeterminate = false;
        }
    }

    private void SetStatus(string text) => SelectedPathText.Text = text;

    // ---------------------------------------------------------------------
    // Tree type filter & search
    //
    // Without filter/search text the tree is lazily populated. When either is active, we walk the whole
    // FsNode tree via FsNodeViewModel.BuildFiltered off the UI thread and swap in a pruned tree.
    // ---------------------------------------------------------------------

    private void TreeFilter_Changed(object? sender, SelectionChangedEventArgs e) =>
        _ = ApplyTreeFilterAsync();

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async Task ApplyTreeFilterAsync()
    {
        if (_vfs is null)
            return;

        VirtualFileSystem vfs = _vfs;
        var typeFilter = TypeFilterCombo.SelectedItem as ResourceTypeFilter ?? ResourceTypeFilter.All;
        string searchText = (SearchBox.Text ?? string.Empty).Trim();
        bool hideLocalized = _settings.HideLocalizedEntries;

        FsNode? previouslySelected = _selectedNode;

        if (typeFilter == ResourceTypeFilter.All && searchText.Length == 0 && !hideLocalized)
        {
            var rootVm = new FsNodeViewModel(vfs.Root, vfs) { IsExpanded = true };
            Tree.ItemsSource = new[] { rootVm };
            SetStatus($"Loaded \"{vfs.BaseDir}\".");
            TryRestoreTreeSelection(rootVm, previouslySelected);
            return;
        }

        SetStatus("Filtering...");

        bool Matches(FsNode node) =>
            (!hideLocalized || !node.IsLocalized) &&
            typeFilter.Matches(node.Name) &&
            (searchText.Length == 0 || MatchesSearch(node, searchText));

        static string? ExtractSubtitleMatch(FsNode node, string searchText)
        {
            if (searchText.Length == 0 || node.ExtendedInfo is not { Length: > 0 } lines)
                return null;

            // Ignore the "[soundName]" header line; we want the actual subtitle text. If the file-name
            // itself matched, no subtitle line is highlighted -- the user already sees the file name.
            if (node.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith('['))
                    continue;
                if (line.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    return line;
            }
            return null;
        }

        static bool MatchesSearch(FsNode node, string searchText)
        {
            if (node.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                return true;

            // Full-text search: any subtitle/dialogue line attached to the node (see XrcStructureReader
            // TypeDialogue -> ExtendedInfo) also counts as a match. Makes the tree filter usable as a
            // "find that line where X says Y" tool for the entire game's dialogue.
            if (node.ExtendedInfo is { Length: > 0 } lines)
            {
                foreach (string line in lines)
                {
                    if (!string.IsNullOrEmpty(line) && line.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        FsNodeViewModel? filteredRoot = await Task.Run(() => FsNodeViewModel.BuildFiltered(
            vfs.Root,
            vfs,
            Matches,
            extractSubtitleMatch: n => ExtractSubtitleMatch(n, searchText)));

        // Only the root auto-expands (same as the normal unfiltered tree) -- deeper matching folders
        // stay collapsed so a broad type filter doesn't force the whole tree open.
        if (filteredRoot is not null)
            filteredRoot.IsExpanded = true;

        Tree.ItemsSource = filteredRoot is null ? [] : new[] { filteredRoot };
        SetStatus(filteredRoot is null ? "No matching files." : $"Filtered: {typeFilter.Label}" +
            (searchText.Length > 0 ? $", \"{searchText}\"" : string.Empty));

        if (filteredRoot is not null)
            TryRestoreTreeSelection(filteredRoot, previouslySelected);
    }

    /// <summary>
    /// Walks the (possibly-lazy) tree rooted at <paramref name="rootVm"/> to the previously-selected
    /// <paramref name="target"/> node, expanding directory VMs as it descends and selecting the final
    /// match. Silently no-ops when the target has been filtered out or was never selected.
    /// </summary>
    private void TryRestoreTreeSelection(FsNodeViewModel rootVm, FsNode? target)
    {
        if (target is null)
            return;

        var path = new List<FsNode>();
        for (FsNode? cursor = target; cursor is not null && cursor.Parent is not null; cursor = cursor.Parent)
            path.Add(cursor);
        path.Reverse();

        FsNodeViewModel cursorVm = rootVm;
        foreach (FsNode segment in path)
        {
            if (!cursorVm.IsExpanded)
                cursorVm.IsExpanded = true;

            FsNodeViewModel? next = cursorVm.Children.FirstOrDefault(c => ReferenceEquals(c.Node, segment));
            if (next is null)
                return;

            cursorVm = next;
        }

        // Deferred to a lower dispatcher priority so Avalonia has finished materializing the tree
        // containers before we ask them to bring the target row into view.
        FsNodeViewModel target_ = cursorVm;
        Dispatcher.UIThread.Post(() =>
        {
            target_.IsExpanded = target_.IsDirectory && target_.IsExpanded;
            BringVmIntoViewAndSelect(target_);
        }, DispatcherPriority.Background);
    }

    private void BringVmIntoViewAndSelect(FsNodeViewModel vm)
    {
        Tree.SelectedItem = vm;
        if (Tree.TreeContainerFromItem(vm) is { } container)
            container.BringIntoView();
    }

    /// <summary>
    /// Called by <see cref="ModFolderWatcher"/> when a file under any <c>xarc/</c> folder appears,
    /// changes, or disappears at runtime. The watcher has already updated <see cref="FsNode.ModPath"/>;
    /// this handler just refreshes the tree indicator and re-loads the current preview when applicable.
    /// </summary>
    private void OnModChanged(ModChangeNotification n)
    {
        RefreshModIndicatorForNode(n.Node);

        // If the user is currently looking at this file, re-decode so they see the change immediately.
        // Live-reload matters most for the Updated case (someone saved a new PNG on top of a modded TM);
        // Added/Removed are handled the same way for consistency.
        if (ReferenceEquals(_selectedNode, n.Node))
            LoadSelectedResource(n.Node);
    }

    /// <summary>
    /// Walks the currently-shown tree, finds the <see cref="FsNodeViewModel"/> wrapping <paramref name="node"/>
    /// (if it's been materialized already) and asks it to re-broadcast its <c>HasMod</c> flag so the
    /// "MOD" pill in the row template updates. Silently no-ops when the VM hasn't been materialized yet --
    /// the pill will show naturally when the row is first rendered.
    /// </summary>
    private void RefreshModIndicatorForNode(FsNode node)
    {
        if (Tree.ItemsSource is not IEnumerable<FsNodeViewModel> roots)
            return;

        foreach (FsNodeViewModel root in roots)
        {
            FsNodeViewModel? match = FindVmForNode(root, node);
            if (match is not null)
            {
                match.RefreshModIndicator();
                return;
            }
        }
    }

    private static FsNodeViewModel? FindVmForNode(FsNodeViewModel vm, FsNode target)
    {
        if (ReferenceEquals(vm.Node, target))
            return vm;
        foreach (FsNodeViewModel child in vm.Children)
        {
            FsNodeViewModel? found = FindVmForNode(child, target);
            if (found is not null)
                return found;
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Menu: File
    // ---------------------------------------------------------------------

    private async void SelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        string? folder = await Dialogs.ShowOpenFolderDialog(this, "Select TLJ Install Folder");
        if (folder is null)
            return;

        _settings.BaseDir = folder;
        _settings.RegisterRecentInstall(folder);
        _settings.Save();
        await InitVfsAsync(folder);
        RefreshRecentInstallsMenu();
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || _currentContent is null)
        {
            await Dialogs.ShowMessageBox(this, "Select a file to export first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        switch (_currentContent)
        {
            case ImageResource { Images.Count: 1 } image:
                await ExportSingleImage(image.Images[0]);
                break;

            case ImageResource image:
                await ExportMultipleImages(image.Images);
                break;

            case TextResource text:
                await ExportText(text.Text, "txt", Path.GetFileNameWithoutExtension(_selectedNode.Name));
                break;

            case SoundResource sound:
                await ExportExtractedFile(sound.TempFilePath);
                break;

            case ExternalVideoResource video:
                await ExportExtractedFile(video.TempFilePath);
                break;

            case ModelResource model:
                await ExportModelAsObj(model);
                break;

            case ErrorResource error:
                await Dialogs.ShowMessageBox(this, error.Message, "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private async void ExportRaw_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || _vfs is null)
        {
            await Dialogs.ShowMessageBox(this, "Select a file to export first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TextResource raw = ResourceLoader.LoadRawForced(_selectedNode, _vfs);
            await ExportText(raw.Text, "txt", Path.GetFileNameWithoutExtension(_selectedNode.Name) + "_raw");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Raw export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BatchExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_vfs is null)
        {
            await Dialogs.ShowMessageBox(this, "Load a TLJ install first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FsNode? sourceRoot = _selectedNode is { NodeType: var t } && (t & FsNodeType.Directory) != 0
            ? _selectedNode
            : _vfs.Root;

        MessageBoxResult formatChoice = await Dialogs.ShowMessageBox(
            this,
            "Export images as PNG (with transparency)?\n\nYes = PNG   No = TGA   Cancel = abort",
            "TLJ Explorer",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (formatChoice == MessageBoxResult.Cancel)
            return;
        BatchImageFormat imageFormat = formatChoice == MessageBoxResult.Yes
            ? BatchImageFormat.Png
            : BatchImageFormat.Tga;

        MessageBoxResult modelFormatChoice = await Dialogs.ShowMessageBox(
            this,
            "Export models as glTF (.glb)?\n\nYes = GLB (rigged/skinned)   No = OBJ (+ .mtl)   Cancel = abort",
            "TLJ Explorer",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (modelFormatChoice == MessageBoxResult.Cancel)
            return;
        BatchModelFormat modelFormat = modelFormatChoice == MessageBoxResult.Yes
            ? BatchModelFormat.Glb
            : BatchModelFormat.Obj;

        string? outputDir = await Dialogs.ShowOpenFolderDialog(
            this, $"Choose output folder for batch export of \"{sourceRoot.GetPath()}\"", _settings.LastExportDir);
        if (outputDir is null)
            return;

        RememberExportFolder(outputDir);
        ScanProgressBar.IsVisible = true;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Minimum = 0;
        ScanProgressBar.Maximum = 1;
        ScanProgressBar.Value = 0;
        CancelBatchButton.IsVisible = true;
        CancelBatchButton.IsEnabled = true;
        SetStatus($"Exporting {sourceRoot.GetPath()} to {outputDir}...");

        VirtualFileSystem vfs = _vfs;
        FsNode sourceNode = sourceRoot;

        _batchExportCts?.Cancel();
        _batchExportCts = new CancellationTokenSource();
        CancellationToken token = _batchExportCts.Token;

        // Progress throttling: a fresh Dispatcher.UIThread.Post per file can flood the UI thread when
        // exporting thousands of assets. Only push a status update every ~40 ms of wall clock.
        DateTime lastUiUpdate = DateTime.MinValue;

        try
        {
            BatchExportSummary summary = await Task.Run(() => BatchExporter.ExportSubtree(
                sourceNode,
                vfs,
                outputDir,
                imageFormat,
                modelFormat,
                progress: p =>
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastUiUpdate).TotalMilliseconds < 40 && p.Index != p.Total)
                        return;
                    lastUiUpdate = now;
                    Dispatcher.UIThread.Post(() =>
                    {
                        ScanProgressBar.Maximum = Math.Max(1, p.Total);
                        ScanProgressBar.Value = p.Index;
                        SetStatus($"{p.Index} / {p.Total}: {p.RelativePath}");
                    });
                },
                cancellationToken: token));

            if (token.IsCancellationRequested)
            {
                SetStatus($"Batch export cancelled after {summary.ExportedCount} file(s).");
            }
            else
            {
                await Dialogs.ShowMessageBox(
                    this,
                    $"Batch export complete.\n\n" +
                    $"Exported: {summary.ExportedCount}\n" +
                    $"Skipped (unsupported): {summary.SkippedCount}\n" +
                    $"Failed: {summary.FailedCount}\n\n" +
                    $"Output folder: {outputDir}",
                    "TLJ Explorer",
                    MessageBoxButton.OK,
                    summary.FailedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                SetStatus($"Batch export finished ({summary.ExportedCount} exported, {summary.FailedCount} failed).");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Batch export cancelled.");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Batch export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Batch export failed.");
        }
        finally
        {
            ScanProgressBar.IsVisible = false;
            CancelBatchButton.IsVisible = false;
            _batchExportCts?.Dispose();
            _batchExportCts = null;
        }
    }

    private void OpenCommandPalette()
    {
        if (_vfs is null)
            return;

        _ = OpenCommandPaletteAsync();
    }

    private async Task OpenCommandPaletteAsync()
    {
        var palette = new CommandPaletteWindow(_vfs!);
        FsNode? selected = await palette.ShowDialog<FsNode?>(this);
        if (selected is null)
            return;

        // Selecting through the tree ensures a11y focus, selection preservation, and status-bar update all
        // work exactly the same as a manual click.
        FsNodeViewModel? rootVm = (Tree.ItemsSource as IEnumerable<FsNodeViewModel>)?.FirstOrDefault();
        if (rootVm is not null)
            TryRestoreTreeSelection(rootVm, selected);
        else
            LoadSelectedResource(selected);
    }

    private async void ExportSubtitles_Click(object? sender, RoutedEventArgs e)
    {
        if (_vfs is null)
        {
            await Dialogs.ShowMessageBox(this, "Load a TLJ install first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? outputDir = await Dialogs.ShowOpenFolderDialog(this, "Choose output folder for subtitle export", _settings.LastExportDir);
        if (outputDir is null)
            return;

        RememberExportFolder(outputDir);

        ScanProgressBar.IsVisible = true;
        ScanProgressBar.IsIndeterminate = true;
        SetStatus("Collecting subtitles...");

        VirtualFileSystem vfs = _vfs;

        try
        {
            (int lineCount, int sceneCount) = await Task.Run(() =>
            {
                IReadOnlyList<SubtitleEntry> entries = SubtitleIndex.Collect(vfs);
                SubtitleIndex.WriteCsv(entries, Path.Combine(outputDir, "dialogue.csv"));
                int scenes = SubtitleIndex.WriteSrtPerScene(entries, Path.Combine(outputDir, "srt"));
                return (entries.Count, scenes);
            });

            SetStatus($"Exported {lineCount} subtitles across {sceneCount} scene(s).");
            await Dialogs.ShowMessageBox(this,
                $"Exported {lineCount} dialogue line(s) across {sceneCount} scene(s):\n\n" +
                $"  {Path.Combine(outputDir, "dialogue.csv")}\n" +
                $"  {Path.Combine(outputDir, "srt")}\\*.srt",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Subtitle export failed.");
            await Dialogs.ShowMessageBox(this, $"Subtitle export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanProgressBar.IsVisible = false;
            ScanProgressBar.IsIndeterminate = false;
        }
    }

    private void CancelBatchButton_Click(object? sender, RoutedEventArgs e)
    {
        _batchExportCts?.Cancel();
        CancelBatchButton.IsEnabled = false;
    }

    /// <summary>
    /// Resolves the tree row a context-menu click applies to. Avalonia's ContextMenu doesn't expose a
    /// PlacementTarget-chain to walk the way WPF's did; since right-clicking a TreeViewItem already
    /// updates TreeView.SelectedItem before the context menu opens, using the current selection directly
    /// is simpler and equally correct here.
    /// </summary>
    private FsNode? ResolveContextTarget(object? sender) => (Tree.SelectedItem as FsNodeViewModel)?.Node;

    private async void TreeContextCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null)
            return;
        try
        {
            await Dialogs.SetClipboardTextAsync(this, node.GetPath());
            SetStatus($"Copied path: {node.GetPath()}");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Could not copy to clipboard:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TreeContextRevealInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null)
            return;

        // Only loose files (not in-archive) live on disk at a real path we can hand to Explorer.
        if ((node.NodeType & FsNodeType.InArchive) != 0 || string.IsNullOrEmpty(node.ArchivePath))
        {
            await Dialogs.ShowMessageBox(this,
                "This entry lives inside a .xarc archive, not as a file on disk -- Explorer can't reveal it. Use Export instead.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.ArchivePath}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(node.ArchivePath)}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"-R \"{node.ArchivePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Could not open file manager:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TreeContextExportItem_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || (node.NodeType & FsNodeType.File) == 0)
        {
            await Dialogs.ShowMessageBox(this, "Select a file to export.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Select the target node first so the existing Export_Click flow (which uses _selectedNode /
        // _currentContent) has the right state, then delegate.
        LoadSelectedResource(node);
        Export_Click(this, new RoutedEventArgs());
    }

    private async void TreeContextViewOriginal_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || (node.NodeType & FsNodeType.File) == 0 || _vfs is null)
            return;

        if (!node.HasMod)
        {
            await Dialogs.ShowMessageBox(this, "This entry has no mod override.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Force-load the archive variant regardless of the LoadMods toggle. The one-shot bypass leaves the
        // global setting alone.
        LoadSelectedResource(node, VirtualFileSystem.OpenVariant.Original);
    }

    private async void TreeContextCompareMod_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || (node.NodeType & FsNodeType.File) == 0 || _vfs is null)
            return;

        if (!node.HasMod)
        {
            await Dialogs.ShowMessageBox(this, "This entry has no mod override to compare against.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ShowCompareView(node);
    }

    private async void TreeContextExtractAsMod_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || _vfs is null || (node.NodeType & FsNodeType.File) == 0)
            return;

        // Only archive entries need a "mod extract" -- loose files are already editable in place. For
        // clarity, refuse instead of silently doing nothing when the entry is already loose.
        if ((node.NodeType & FsNodeType.InArchive) == 0)
        {
            await Dialogs.ShowMessageBox(this,
                "This entry is already a loose file on disk -- open it directly instead of extracting.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(node.ArchivePath))
            return;

        string? archiveDir = Path.GetDirectoryName(node.ArchivePath);
        if (string.IsNullOrEmpty(archiveDir))
            return;
        string modDir = Path.Combine(archiveDir, "xarc");
        string modPath = Path.Combine(modDir, node.Name);

        if (File.Exists(modPath))
        {
            MessageBoxResult overwrite = await Dialogs.ShowMessageBox(this,
                $"A mod file already exists here:\n\n{modPath}\n\nOverwrite it with the archived original?",
                "TLJ Explorer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (overwrite != MessageBoxResult.Yes)
                return;
        }

        try
        {
            Directory.CreateDirectory(modDir);
            // Force the archive variant so extracting an already-modded entry writes the ORIGINAL bytes,
            // not the current mod (which is the intent: reset the mod to a known baseline for editing).
            using (Stream src = _vfs.OpenFile(node, VirtualFileSystem.OpenVariant.Original))
            using (var dest = new FileStream(modPath, FileMode.Create, FileAccess.Write))
            {
                src.CopyTo(dest);
            }

            node.ModPath = modPath;
            RefreshModIndicatorForNode(node);
            SetStatus($"Extracted as mod: {modPath}");

            // Reload the current view so the MOD pill lights up on the tree row and the A/B toggle appears
            // if this happens to be the selected file.
            if (ReferenceEquals(_selectedNode, node))
                LoadSelectedResource(node);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Extract failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TreeContextPlayLocalized_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || node.Parent is null || _vfs is null)
            return;

        // Find a sibling file whose name matches (case-insensitive) but whose IsLocalized flag differs.
        // English variant is IsLocalized=false; the "other" is the localized variant.
        FsNode? sibling = node.Parent.Children.FirstOrDefault(c =>
            (c.NodeType & FsNodeType.File) != 0 &&
            !ReferenceEquals(c, node) &&
            c.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase) &&
            c.IsLocalized != node.IsLocalized);

        if (sibling is null)
        {
            await Dialogs.ShowMessageBox(this,
                node.IsLocalized
                    ? "No English (flag=0) sibling found for this entry."
                    : "No localised (flag=1) sibling found for this entry.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoadSelectedResource(sibling);
    }

    private async void TreeContextBatchExport_Click(object? sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null)
            return;

        // If the user right-clicked a file, batch-export its parent folder -- that matches Explorer's
        // "extract this and everything around it" idiom users are likely to reach for here.
        FsNode target = (node.NodeType & FsNodeType.Directory) != 0 ? node : (node.Parent ?? node);

        // Temporarily fixup _selectedNode so BatchExport_Click uses the right folder, then restore.
        FsNode? previous = _selectedNode;
        _selectedNode = target;
        try
        {
            BatchExport_Click(this, new RoutedEventArgs());
        }
        finally
        {
            _selectedNode = previous;
        }

        await Task.CompletedTask;
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task ExportModelAsObj(ModelResource model)
    {
        string defaultStem = _selectedNode is not null
            ? Path.GetFileNameWithoutExtension(_selectedNode.Name)
            : "model";

        string? path = await Dialogs.ShowSaveFileDialog(
            this, "Export Model", SanitizeFileName(defaultStem) + ".glb",
            [new Avalonia.Platform.Storage.FilePickerFileType("Binary glTF") { Patterns = ["*.glb"] },
             new Avalonia.Platform.Storage.FilePickerFileType("Wavefront OBJ") { Patterns = ["*.obj"] }],
            _settings.LastExportDir);
        if (path is null)
            return;

        AniAnimation? bindPose = TryLoadCurrentBindPose();

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".obj")
            {
                ObjWriter.Write(model.Model, path, bindPose);
            }
            else
            {
                var options = new GlbWriteOptions
                {
                    BindPose = bindPose,
                    TextureResolver = _vfs is not null
                        ? ModelTextureResolver.Create(_vfs, model.SourceNode)
                        : null,
                };
                GlbWriter.Write(model.Model, path, options);
            }
            RememberExportFolder(path);

            if (bindPose is null && model.Model.Skeleton.Length > 1)
            {
                await Dialogs.ShowMessageBox(this,
                    "Exported without a bind-pose animation. The mesh will look like a jumble of bone-local fragments in Blender.\n\n" +
                    "Select an animation (Animation dropdown, or enable 'Include whole install') before exporting to get a properly-posed and skinned model.",
                    "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Looks for a <c>.cir</c> in the same folder as <paramref name="aniNode"/>, loads it into the model
    /// viewer, and preselects <paramref name="aniNode"/> as the active animation. Returns <c>false</c>
    /// when there's no sibling model (in which case the caller should fall back to the raw text dump).
    /// </summary>
    private bool TryOpenAnimationOnModel(FsNode aniNode)
    {
        if (_vfs is null || aniNode.Parent is null)
            return false;

        FsNode? cirNode = _vfs.GetFiles(aniNode.Parent).FirstOrDefault(f =>
            string.Equals(Path.GetExtension(f.Name), ".cir", StringComparison.OrdinalIgnoreCase));
        if (cirNode is null)
            return false;

        try
        {
            CirModel model;
            using (Stream s = _vfs.OpenFile(cirNode))
                model = CirDecoder.Decode(s);

            // Pretend the user clicked the .cir: keep _selectedNode pointing at the .ani so the tree's
            // status and current selection reflect their actual click, but hand the model + ani to the
            // viewer directly instead of routing through LoadSelectedResource (which would recurse on
            // the .ani special-case).
            _currentContent = new ModelResource(model, cirNode);
            ShowContent(_currentContent);

            // Force the animation dropdown to the .ani the user actually clicked. LoadModelIntoViewer
            // has already applied whatever it thought the default should be; override it.
            ApplyAnimationSelection(aniNode, autoPlay: true);
            SyncAnimationComboWith(aniNode);
            ModelNoAnimationNote.IsVisible = false;

            SetStatus($"{aniNode.GetPath()}  ->  {cirNode.Name}  (animation applied)");
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessageBox(this, $"Could not open model for animation:\n{ex.Message}",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void SyncAnimationComboWith(FsNode aniNode)
    {
        _updatingModelCombos = true;
        try
        {
            if (AnimationSelectorCombo.ItemsSource is IEnumerable<ModelPickItem> items)
            {
                ModelPickItem? match = items.FirstOrDefault(i => ReferenceEquals(i.Node, aniNode));
                if (match is not null)
                    AnimationSelectorCombo.SelectedItem = match;
            }
        }
        finally
        {
            _updatingModelCombos = false;
        }
    }

    /// <summary>Decodes the currently-selected ANI (if any) for use as a bind pose during export.</summary>
    private AniAnimation? TryLoadCurrentBindPose()
    {
        if (_vfs is null || _currentAnimationNode is null)
            return null;
        try
        {
            using Stream s = _vfs.OpenFile(_currentAnimationNode);
            return AniDecoder.Decode(s);
        }
        catch
        {
            return null;
        }
    }

    private async Task ExportSingleImage((string Name, DecodedImage Image) entry)
    {
        string? path = await Dialogs.ShowSaveFileDialog(
            this, "Export Image", SanitizeFileName(entry.Name) + ".png",
            [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] },
             new Avalonia.Platform.Storage.FilePickerFileType("Targa image") { Patterns = ["*.tga"] }],
            _settings.LastExportDir);
        if (path is null)
            return;

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".tga")
                TgaWriter.Write(entry.Image, path);
            else
                PngWriter.Write(entry.Image, path);
            RememberExportFolder(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportMultipleImages(IReadOnlyList<(string Name, DecodedImage Image)> images)
    {
        MessageBoxResult choice = await Dialogs.ShowMessageBox(
            this,
            "Export as PNG (with transparency)?\n\nYes = PNG   No = TGA   Cancel = abort",
            "TLJ Explorer",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
            return;
        bool asPng = choice == MessageBoxResult.Yes;
        string extension = asPng ? ".png" : ".tga";

        string? folder = await Dialogs.ShowOpenFolderDialog(this, "Select destination folder for exported images", _settings.LastExportDir);
        if (folder is null)
            return;

        RememberExportFolder(folder);

        int exported = 0;
        try
        {
            foreach (var (name, image) in images)
            {
                string fileName = SanitizeFileName(string.IsNullOrEmpty(name) ? $"image_{exported}" : name) + extension;
                string path = Path.Combine(folder, fileName);
                if (asPng)
                    PngWriter.Write(image, path);
                else
                    TgaWriter.Write(image, path);
                exported++;
            }

            await Dialogs.ShowMessageBox(this, $"Exported {exported} image(s) to:\n{folder}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Export failed after {exported} image(s):\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportText(string text, string extension, string defaultNameWithoutExtension)
    {
        string? path = await Dialogs.ShowSaveFileDialog(
            this, "Export Text", SanitizeFileName(defaultNameWithoutExtension) + "." + extension,
            [new Avalonia.Platform.Storage.FilePickerFileType($"Text file (*.{extension})") { Patterns = [$"*.{extension}"] }],
            _settings.LastExportDir);
        if (path is null)
            return;

        try
        {
            File.WriteAllText(path, text);
            RememberExportFolder(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportExtractedFile(string tempFilePath)
    {
        string extension = Path.GetExtension(tempFilePath).TrimStart('.');
        string defaultName = (_selectedNode is not null ? Path.GetFileNameWithoutExtension(_selectedNode.Name) : "export") + "." + extension;

        string? path = await Dialogs.ShowSaveFileDialog(
            this, "Export", defaultName,
            [new Avalonia.Platform.Storage.FilePickerFileType($"{extension.ToUpperInvariant()} file (*.{extension})") { Patterns = [$"*.{extension}"] }],
            _settings.LastExportDir);
        if (path is null)
            return;

        try
        {
            File.Copy(tempFilePath, path, overwrite: true);
            RememberExportFolder(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RememberExportFolder(string chosenPath)
    {
        string? dir = Directory.Exists(chosenPath) ? chosenPath : Path.GetDirectoryName(chosenPath);
        if (string.IsNullOrEmpty(dir) || dir == _settings.LastExportDir)
            return;

        _settings.LastExportDir = dir;
        _settings.Save();
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            buffer[i] = invalid.Contains(name[i]) ? '_' : name[i];

        string result = new(buffer);
        return string.IsNullOrWhiteSpace(result) ? "export" : result;
    }

    // ---------------------------------------------------------------------
    // Menu: Options
    // ---------------------------------------------------------------------

    private void InitializeOptionsMenu()
    {
        AutoPlaySoundMenuItem.IsChecked = _settings.AutoPlaySound;
        AutoPlayVideoMenuItem.IsChecked = _settings.AutoPlayVideo;
        LoopSoundMenuItem.IsChecked = _settings.LoopSoundPlayback;
        RefreshRecentInstallsMenu();

        ModelWireframeCheck.IsChecked = _settings.ModelViewerWireframe;
        ModelViewerHost.ShowWireframe = _settings.ModelViewerWireframe;

        string bg = _settings.ModelViewerBackground;
        ModelBackgroundCombo.SelectedIndex = bg switch { "Light" => 1, "Transparent" => 2, _ => 0 };
        ApplyModelBackgroundPreset(bg);
        ApplyTheme(_settings.Theme);
    }

    internal void ApplyTheme(string theme)
    {
        Avalonia.Styling.ThemeVariant variant = theme switch
        {
            "Light" => Avalonia.Styling.ThemeVariant.Light,
            "System" => Avalonia.Styling.ThemeVariant.Default,
            _ => Avalonia.Styling.ThemeVariant.Dark,
        };
        Application.Current!.RequestedThemeVariant = variant;
    }

    private void OpenSettings_Click(object? sender, RoutedEventArgs e) =>
        SettingsOverlay.Show(this, _settings);

    private void AutoPlaySound_Click(object? sender, RoutedEventArgs e)
    {
        _settings.AutoPlaySound = AutoPlaySoundMenuItem.IsChecked;
        _settings.Save();
    }

    private void AutoPlayVideo_Click(object? sender, RoutedEventArgs e)
    {
        _settings.AutoPlayVideo = AutoPlayVideoMenuItem.IsChecked;
        _settings.Save();
    }

    private void LoopSound_Click(object? sender, RoutedEventArgs e)
    {
        _settings.LoopSoundPlayback = LoopSoundMenuItem.IsChecked;
        _settings.Save();
    }

    private void RefreshRecentInstallsMenu()
    {
        RecentInstallsMenu.Items.Clear();
        RecentInstallsMenu.IsEnabled = _settings.RecentInstalls.Count > 0;
        foreach (string path in _settings.RecentInstalls)
        {
            var item = new MenuItem { Header = path, Tag = path };
            item.Click += RecentInstallItem_Click;
            RecentInstallsMenu.Items.Add(item);
        }
    }

    private async void RecentInstallItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path })
            return;
        if (!Directory.Exists(path))
        {
            _settings.RecentInstalls.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            RefreshRecentInstallsMenu();
            await Dialogs.ShowMessageBox(this, $"Install folder no longer exists:\n{path}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.BaseDir = path;
        _settings.RegisterRecentInstall(path);
        _settings.Save();
        await InitVfsAsync(path);
        RefreshRecentInstallsMenu();
    }

    // Setting-change callbacks invoked by the Settings dialog after it has already updated
    // and persisted the underlying _settings value. Each one only carries the side-effects
    // needed to make the change visible in the running UI (reload the current preview,
    // reapply the tree filter, re-open the VFS with a new mods folder, ...).

    internal void OnShowMipMapsChanged()
    {
        // Affects .tm decoding directly: re-decode the current selection if it's still a TM texture.
        if (_selectedNode is not null && string.Equals(Path.GetExtension(_selectedNode.Name), ".tm", StringComparison.OrdinalIgnoreCase))
        {
            LoadSelectedResource(_selectedNode);
        }
    }

    internal void OnHideLocalizedChanged() => _ = ApplyTreeFilterAsync();

    internal void OnLoadAssetModsChanged()
    {
        if (_vfs is not null)
            _vfs.LoadMods = _settings.LoadAssetMods;

        // Re-load whatever the user is currently looking at so the change is visible immediately.
        if (_selectedNode is not null)
        {
            if ((_selectedNode.NodeType & FsNodeType.File) != 0)
                LoadSelectedResource(_selectedNode);
            else
                LoadSelectedFolder(_selectedNode);
        }
    }

    internal async Task SelectExternalModsFolderAsync()
    {
        string? folder = await Dialogs.ShowOpenFolderDialog(this, "Select External Mods Folder", _settings.ExternalModsDir);
        if (folder is null)
            return;

        _settings.ExternalModsDir = folder;
        _settings.Save();

        if (!string.IsNullOrEmpty(_settings.BaseDir))
            await InitVfsAsync(_settings.BaseDir);
    }

    internal async Task ClearExternalModsFolderAsync()
    {
        _settings.ExternalModsDir = null;
        _settings.Save();

        if (!string.IsNullOrEmpty(_settings.BaseDir))
            await InitVfsAsync(_settings.BaseDir);
    }

    /// <summary>Re-init the VFS against the current BaseDir + settings, if a BaseDir is set.
    /// Used by the settings panel when a path field is edited directly.</summary>
    internal async Task ReloadVfsIfLoadedAsync()
    {
        if (!string.IsNullOrEmpty(_settings.BaseDir))
            await InitVfsAsync(_settings.BaseDir);
    }

    internal void PromptForFfmpeg() => _ = PromptForFfmpegPathAsync();

    /// <summary>
    /// Verifies <see cref="AppSettings.FfmpegPath"/> points at an existing ffmpeg executable. When
    /// missing, shows a message with download links and offers a file picker to locate it. Returns true
    /// only if a valid ffmpeg is available after the interaction.
    /// </summary>
    private async Task<bool> EnsureFfmpegAvailable()
    {
        if (File.Exists(_settings.FfmpegPath))
            return true;

        const string message =
            "ffmpeg was not found -- it's required to play Bink/Smacker videos.\n\n" +
            "Download a build from one of:\n" +
            "  - https://www.gyan.dev/ffmpeg/builds/  (Windows: \"release full shared\" or \"essentials shared\")\n" +
            "  - https://github.com/BtbN/FFmpeg-Builds/releases  (\"...shared...\" builds)\n" +
            "  - your Linux distro's package manager (e.g. apt install ffmpeg)\n\n" +
            "Extract it somewhere permanent, then click OK to point the app at the ffmpeg binary.";

        MessageBoxResult res = await Dialogs.ShowMessageBox(
            this, message, "ffmpeg not found",
            MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (res != MessageBoxResult.OK)
            return false;

        return await PromptForFfmpegPathAsync();
    }

    /// <summary>Opens a file picker for the ffmpeg executable and, if the user selects one, saves it to
    /// settings. Returns true when a valid ffmpeg is set at the end.</summary>
    private async Task<bool> PromptForFfmpegPathAsync()
    {
        string suggestedDir = File.Exists(_settings.FfmpegPath) ? Path.GetDirectoryName(_settings.FfmpegPath)! : "";
        string? path = await Dialogs.ShowOpenFileDialog(this, "Locate ffmpeg", null, suggestedDir);
        if (path is null)
            return File.Exists(_settings.FfmpegPath);

        _settings.FfmpegPath = path;
        _settings.Save();
        return true;
    }

    internal async void RunExternalModsDiagnostic()
    {
        if (_vfs is null)
        {
            await Dialogs.ShowMessageBox(this, "Load a TLJ install first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string report = ExternalModsDiagnostic.Build(_vfs);

        ClearContentPanels();
        TextPanel.Text = report;
        TextViewer.IsVisible = true;
        SetStatus("External mods diagnostic report shown in the viewer.");
    }

    // ---------------------------------------------------------------------
    // Tree selection
    // ---------------------------------------------------------------------

    private void Tree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Tree.SelectedItem is not FsNodeViewModel { IsPlaceholder: false } vm)
            return;

        SetStatus(vm.Node.GetPath());
        PersistLastSelectedPath(vm.Node);

        if (vm.IsFile)
        {
            LoadSelectedResource(vm.Node);
        }
        else
        {
            LoadSelectedFolder(vm.Node);
        }
    }

    private void PersistLastSelectedPath(FsNode node)
    {
        if (_vfs is null)
            return;
        string path = node.GetPath();
        if (_settings.LastSelectedPath.TryGetValue(_vfs.BaseDir, out string? existing) && existing == path)
            return;
        _settings.LastSelectedPath[_vfs.BaseDir] = path;
        _settings.Save();
    }

    /// <summary>
    /// A folder selection: if the folder is an XRC scene ("April's room" etc.), render it. Loading is
    /// async because ffmpeg has to extract PNG frames for every animated overlay. Selection may change
    /// mid-load; a per-load generation guards against a stale scene overwriting a newer one.
    /// </summary>
    private async void LoadSelectedFolder(FsNode folder)
    {
        if (_vfs is null)
        {
            _selectedNode = null;
            _currentContent = null;
            ClearContentPanels();
            return;
        }

        StopSound();
        _selectedNode = folder;
        _currentContent = null;

        int generation = ++_sceneLoadGeneration;
        ClearContentPanels();
        ScenePanel.IsVisible = true;
        SceneStatusText.Text = "Rendering scene...";
        SceneCanvas.Children.Clear();
        SceneCanvas.Children.Add(SceneBaseImage);
        SceneBaseImage.Source = null;

        VirtualFileSystem vfs = _vfs;
        AppSettings settings = _settings;
        TempFileTracker tempFiles = _tempFiles;

        ResourceContent? scene = await Task.Run(
            () => ResourceLoader.LoadScene(folder, vfs, settings, tempFiles));

        if (generation != _sceneLoadGeneration)
            return;

        if (scene is null)
        {
            ClearContentPanels();
            return;
        }

        _currentContent = scene;
        ShowContent(scene);
    }

    private async void LoadSelectedResource(FsNode node, VirtualFileSystem.OpenVariant variant = VirtualFileSystem.OpenVariant.Preferred)
    {
        if (_vfs is null)
            return;

        StopSound();
        _selectedNode = node;

        // Special-case .ani: users almost always want to *see the animation* on its model, not read the
        // decoded keyframe dump. Redirect to the sibling .cir with this animation preselected. The raw
        // text dump remains one click away via the "Source" button in the model panel.
        if (string.Equals(Path.GetExtension(node.Name), ".ani", StringComparison.OrdinalIgnoreCase) &&
            TryOpenAnimationOnModel(node))
        {
            return;
        }

        int generation = ++_resourceLoadGeneration;
        VirtualFileSystem vfs = _vfs;
        AppSettings settings = _settings;
        TempFileTracker tempFiles = _tempFiles;
        SetStatus($"{node.GetPath()}  -  loading...");

        ResourceContent content;
        try
        {
            content = await Task.Run(() => ResourceLoader.Load(node, vfs, settings, tempFiles, variant));
        }
        catch (Exception ex)
        {
            // ResourceLoader.Load already turns most decode failures into ErrorResource, but a truly
            // unexpected throw (e.g. thread-abort during shutdown) shouldn't crash the app.
            content = new ErrorResource($"Failed to load \"{node.GetPath()}\":\n{ex.Message}");
        }

        // Drop the result if the user has moved on to a different selection in the meantime.
        if (generation != _resourceLoadGeneration)
            return;

        _currentContent = content;
        ShowContent(content);

        string variantSuffix = variant switch
        {
            VirtualFileSystem.OpenVariant.Original when node.HasMod => "  [original]",
            VirtualFileSystem.OpenVariant.Mod when node.HasMod => "  [modded]",
            _ => node.HasMod && vfs.LoadMods ? "  [modded]" : "",
        };
        SetStatus($"{node.GetPath()}  -  {FormatContentMetadata(node, content)}{variantSuffix}");
    }

    /// <summary>
    /// Builds a one-line summary for the status bar: file size plus content-specific details (image
    /// dimensions, sound duration, model face count, etc.). Used to give the user a quick "what am I
    /// looking at" glance without having to open the properties dialog.
    /// </summary>
    private static string FormatContentMetadata(FsNode node, ResourceContent content)
    {
        string sizeText = FormatByteSize(node.Size);
        string details = content switch
        {
            ImageResource { Images.Count: 1 } single =>
                $"{single.Images[0].Image.Width}x{single.Images[0].Image.Height}",
            ImageResource multi =>
                $"{multi.Images.Count} sub-images",
            ModelResource m =>
                $"{m.Model.Materials.Length} material(s), {SumFaceTriangles(m.Model)} triangle(s), {m.Model.Skeleton.Length} bone(s)",
            SoundResource => "audio",
            ExternalVideoResource v => v.Kind,
            TextResource t => $"{t.Text.Length:N0} chars",
            ErrorResource => "load failed",
            _ => "",
        };

        return string.IsNullOrEmpty(details) ? sizeText : $"{sizeText}  |  {details}";
    }

    private static int SumFaceTriangles(CirModel model)
    {
        if (model.Groups.Length == 0)
            return 0;
        int total = 0;
        foreach (CirFace face in model.Groups[0].Faces)
            total += face.Triangles.Length;
        return total;
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return $"{mb:0.##} MB";
        return $"{mb / 1024.0:0.##} GB";
    }

    // ---------------------------------------------------------------------
    // Content panel switching
    // ---------------------------------------------------------------------

    private void ClearContentPanels()
    {
        ImagePanel.IsVisible = false;
        TextViewer.IsVisible = false;
        AniSourceHeader.IsVisible = false;
        SoundPanel.IsVisible = false;
        VideoPanel.IsVisible = false;
        ModelPanel.IsVisible = false;
        ScenePanel.IsVisible = false;
        ImageComparePanel.IsVisible = false;

        StopVideo();
        StopSceneAnimation();

        // Bump the generation so any in-flight background texture load for the previous model is dropped.
        _modelLoadGeneration++;
        ModelViewerHost.ClearModel();

        _currentModel = null;
        _currentModelNode = null;
        _currentAnimationNode = null;
    }

    private List<(string Name, DecodedImage Image)> _currentImages = [];
    private DecodedImage? _currentDisplayedImage;
    private double _imageZoom = 1.0;
    private bool _imageFitToWindow;
    private Point? _panStart;
    private double _panStartHOffset;
    private double _panStartVOffset;

    private const double MinZoom = 0.25;
    private const double MaxZoom = 16.0;

    private void ShowContent(ResourceContent content)
    {
        ClearContentPanels();

        switch (content)
        {
            case ImageResource images:
                ShowImages(images.Images);
                break;

            case TextResource text:
                TextPanel.Text = text.Text;
                TextViewer.IsVisible = true;
                break;

            case SoundResource sound:
                ShowSound(sound);
                break;

            case ExternalVideoResource video:
                ShowVideo(video);
                break;

            case ErrorResource error:
                TextPanel.Text = error.Message;
                TextViewer.IsVisible = true;
                break;

            case ModelResource model:
                ShowModel(model);
                break;

            case SceneResource scene:
                ShowScene(scene);
                break;

            case ImageCompareResource compare:
                ShowImageCompare(compare);
                break;
        }
    }

    /// <summary>
    /// Decodes the original and the mod variants of <paramref name="node"/> and swaps in the compare
    /// panel. Only images (XMG/TM) are supported by this view; other file types show a message.
    /// </summary>
    private async Task ShowCompareView(FsNode node)
    {
        if (_vfs is null)
            return;

        string ext = Path.GetExtension(node.Name).ToLowerInvariant();
        if (ext is not (".xmg" or ".tm"))
        {
            await Dialogs.ShowMessageBox(this,
                "The vertical-wipe compare view currently supports images (.xmg, .tm) only. Use the A/B toggle in the sound/model panels for other asset kinds.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DecodedImage? original = TryDecodeSingleImage(node, VirtualFileSystem.OpenVariant.Original, ext);
        DecodedImage? modded = TryDecodeSingleImage(node, VirtualFileSystem.OpenVariant.Mod, ext);
        if (original is null || modded is null)
        {
            await Dialogs.ShowMessageBox(this, "Could not decode both variants for comparison.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedNode = node;
        var compare = new ImageCompareResource(
            OriginalLabel: node.DisplayName,
            Original: original,
            ModdedLabel: node.DisplayName,
            Modded: modded);
        _currentContent = compare;
        ShowContent(compare);
    }

    private DecodedImage? TryDecodeSingleImage(FsNode node, VirtualFileSystem.OpenVariant variant, string ext)
    {
        if (_vfs is null) return null;
        try
        {
            using Stream s = _vfs.OpenFile(node, variant);
            if (ext == ".xmg")
                // Mods override XMGs with PNGs, so we may be handed either byte layout on the mod side.
                return PngToDecodedImage.DecodeXmgOrPng(s, XmgDecoder.Decode);

            IReadOnlyList<TmEntry> entries = TmDecoder.Decode(s, useMipMap: false);
            return entries.Count > 0 ? entries[0].Image : null;
        }
        catch
        {
            return null;
        }
    }

    private Bitmap? _compareOriginalBitmap;
    private Bitmap? _compareModBitmap;
    private double _compareWipeX;
    private double _compareZoom = 1.0;
    private bool _compareFitToWindow;
    private int _compareCanvasWidth;
    private int _compareCanvasHeight;
    private bool _syncingCompareScroll;

    private void ShowImageCompare(ImageCompareResource compare)
    {
        _compareOriginalBitmap = ToBitmap(compare.Original);
        _compareModBitmap = ToBitmap(compare.Modded);

        // Canvas = the max of each axis so both images can display at their native pixel grid without one
        // being sub-sampled. When the mod is higher-res (typical for HD packs), the original scales up via
        // Stretch=Fill to sit on the same grid, so wipe/side-by-side comparisons line up 1:1.
        _compareCanvasWidth = Math.Max(compare.Original.Width, compare.Modded.Width);
        _compareCanvasHeight = Math.Max(compare.Original.Height, compare.Modded.Height);

        ImageCompareOriginalImage.Source = _compareOriginalBitmap;
        ImageCompareModImage.Source = _compareModBitmap;
        ImageCompareOriginalImage.Width = _compareCanvasWidth; ImageCompareOriginalImage.Height = _compareCanvasHeight;
        ImageCompareModImage.Width = _compareCanvasWidth; ImageCompareModImage.Height = _compareCanvasHeight;
        ImageCompareStage.Width = _compareCanvasWidth; ImageCompareStage.Height = _compareCanvasHeight;

        ImageCompareSideOriginal.Source = _compareOriginalBitmap;
        ImageCompareSideMod.Source = _compareModBitmap;
        ImageCompareSideOriginal.Width = _compareCanvasWidth; ImageCompareSideOriginal.Height = _compareCanvasHeight;
        ImageCompareSideMod.Width = _compareCanvasWidth; ImageCompareSideMod.Height = _compareCanvasHeight;

        _compareWipeX = _compareCanvasWidth / 2.0;
        // Panel is still hidden at this point, so the compare ScrollViewers have no Bounds yet --
        // ComputeCompareDefaultZoom would fall back to 1.0 and crop large images. Defer the fit
        // calculation until after Avalonia lays the panel out.
        ApplyCompareDefaultZoomWhenReady();
        UpdateCompareClip();

        ImageCompareLabel.Text = $"Comparing {compare.OriginalLabel} -- original {compare.Original.Width}x{compare.Original.Height}, " +
                                 $"mod {compare.Modded.Width}x{compare.Modded.Height}, canvas {_compareCanvasWidth}x{_compareCanvasHeight}";

        ImageComparePanel.IsVisible = true;
        ApplyImageCompareMode();
    }

    /// <summary>Target on-screen width in pixels for the visible divider bar. Stays constant regardless of zoom.</summary>
    private const double CompareDividerScreenWidth = 2.0;
    /// <summary>Target on-screen width in pixels for the transparent grab area around the divider.</summary>
    private const double CompareDividerHandleScreenWidth = 14.0;

    private void UpdateCompareClip()
    {
        if (_compareOriginalBitmap is null)
            return;
        double x = Math.Max(0, Math.Min(_compareCanvasWidth, _compareWipeX));

        // Dual clip: ORIGINAL renders only on the LEFT of the divider, MOD only on the RIGHT. Because
        // neither image draws over the other's half, transparent pixels don't reveal a stretched low-res
        // version of the other side.
        _imageCompareOriginalClip.Rect = new Rect(0, 0, x, _compareCanvasHeight);
        _imageCompareModClip.Rect = new Rect(x, 0, _compareCanvasWidth - x, _compareCanvasHeight);

        // The divider and its hit-area live inside the zoomed stage, so their local (unscaled) width has to
        // be divided by the current zoom to hold a constant on-screen pixel size. Without this, zooming
        // out to say 0.1x reduces a 2-unit bar to 0.2 screen pixels -- invisible.
        double zoom = Math.Max(_compareZoom, 1e-4);
        double dividerLocalWidth = CompareDividerScreenWidth / zoom;
        double handleLocalWidth = CompareDividerHandleScreenWidth / zoom;

        ImageCompareDivider.Width = dividerLocalWidth;
        ImageCompareDivider.Margin = new Thickness(x - (dividerLocalWidth / 2), 0, 0, 0);
        ImageCompareDivider.Height = _compareCanvasHeight;

        ImageCompareDividerHandle.Width = handleLocalWidth;
        ImageCompareDividerHandle.Margin = new Thickness(x - (handleLocalWidth / 2), 0, 0, 0);
        ImageCompareDividerHandle.Height = _compareCanvasHeight;
    }

    private void ApplyImageCompareMode()
    {
        bool wipe = ImageCompareWipeMode.IsChecked == true;
        ImageCompareWipeScroll.IsVisible = wipe;
        ImageCompareSideBySideContainer.IsVisible = !wipe;
    }

    private void ImageCompareMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (ImageComparePanel.IsVisible)
            ApplyImageCompareMode();
    }

    // -------------------- Wipe drag (on the transparent divider handle overlay) --------------------

    private bool _compareWipeDragging;

    private void ImageCompareDividerHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _compareWipeDragging = true;
        _compareWipeX = e.GetPosition(ImageCompareStage).X;
        UpdateCompareClip();
        e.Pointer.Capture(ImageCompareDividerHandle);
        // Handled=true prevents the click from bubbling up to the ScrollViewer's pan handler, which would
        // otherwise start a pan on the same press.
        e.Handled = true;
    }

    private void ImageCompareDividerHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_compareWipeDragging)
            return;
        _compareWipeX = e.GetPosition(ImageCompareStage).X;
        UpdateCompareClip();
    }

    private void ImageCompareDividerHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _compareWipeDragging = false;
        e.Pointer.Capture(null);
    }

    // -------------------- Pan (left-drag on any compare ScrollViewer) --------------------

    private Point? _comparePanStart;
    private double _comparePanStartH;
    private double _comparePanStartV;
    private ScrollViewer? _comparePanTarget;

    private void ImageComparePanScroll_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer sv)
            return;
        if (e.GetCurrentPoint(sv).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        // Don't hijack clicks that landed on the wipe-divider handle -- those belong to the wipe drag,
        // not to panning.
        if (e.Source is Visual src && IsInDividerHandle(src))
            return;

        _comparePanTarget = sv;
        _comparePanStart = e.GetPosition(sv);
        _comparePanStartH = sv.Offset.X;
        _comparePanStartV = sv.Offset.Y;
        e.Pointer.Capture(sv);
        sv.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    /// <summary>True when <paramref name="src"/> is (or is nested under) the wipe divider handle.</summary>
    private bool IsInDividerHandle(Visual src)
    {
        for (Visual? cursor = src; cursor is not null; cursor = cursor.GetVisualParent())
        {
            if (ReferenceEquals(cursor, ImageCompareDividerHandle))
                return true;
        }
        return false;
    }

    private void ImageComparePanScroll_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_comparePanStart is not { } start || _comparePanTarget is null)
            return;
        if (!e.GetCurrentPoint(_comparePanTarget).Properties.IsLeftButtonPressed)
            return;

        Point p = e.GetPosition(_comparePanTarget);
        _comparePanTarget.Offset = new Vector(
            _comparePanStartH - (p.X - start.X),
            _comparePanStartV - (p.Y - start.Y));
    }

    private void ImageComparePanScroll_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _comparePanStart = null;
        if (_comparePanTarget is not null)
            _comparePanTarget.Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
        _comparePanTarget = null;
    }

    private void ImageCompareClose_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is not null)
            LoadSelectedResource(_selectedNode);
    }

    // ---------------- Zoom & pan (shared between wipe and side-by-side) ----------------

    private const double CompareMinZoom = 0.1;
    private const double CompareMaxZoom = 16.0;

    private bool _syncingCompareZoomSlider;

    private void SetCompareZoom(double zoom)
    {
        _compareZoom = Math.Clamp(zoom, CompareMinZoom, CompareMaxZoom);

        // See SetZoom: the slider raises ValueChanged during InitializeComponent, before named XAML
        // fields are wired up. Bail out until the window is initialized.
        if (!_initialized)
            return;

        _imageCompareStageScale.ScaleX = _compareZoom;
        _imageCompareStageScale.ScaleY = _compareZoom;
        _imageCompareSideOriginalScale.ScaleX = _compareZoom;
        _imageCompareSideOriginalScale.ScaleY = _compareZoom;
        _imageCompareSideModScale.ScaleX = _compareZoom;
        _imageCompareSideModScale.ScaleY = _compareZoom;
        ImageCompareZoomLabel.Text = $"{_compareZoom * 100:0}%";
        _syncingCompareZoomSlider = true;
        try { ImageCompareZoomSlider.Value = _compareZoom * 100.0; }
        finally { _syncingCompareZoomSlider = false; }

        // Divider width is expressed in local (unscaled) coords, so it needs recomputing every zoom change
        // to stay at a constant on-screen size.
        if (_compareCanvasWidth > 0)
            UpdateCompareClip();
    }

    private void ImageCompareZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        _compareFitToWindow = false;
        SetCompareZoom(_compareZoom * 1.25);
    }
    private void ImageCompareZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        _compareFitToWindow = false;
        SetCompareZoom(_compareZoom / 1.25);
    }
    private void ImageCompareZoomReset_Click(object? sender, RoutedEventArgs e) => ApplyCompareDefaultZoomWhenReady();

    /// <summary>
    /// Default zoom for the compare view: fit the shared canvas inside whichever ScrollViewer is
    /// currently visible (wipe or the first side-by-side pane), capped at 8x. Same rule as the main
    /// image panel and the scene panel.
    /// </summary>
    private double ComputeCompareDefaultZoom()
    {
        if (_compareCanvasWidth <= 0 || _compareCanvasHeight <= 0)
            return 1.0;

        // Prefer the currently-visible ScrollViewer for size probing so the fit works out in both modes.
        ScrollViewer probe = ImageCompareWipeScroll.IsVisible ? ImageCompareWipeScroll : ImageCompareSideOriginalScroll;

        double vpW = probe.Bounds.Width;
        double vpH = probe.Bounds.Height;
        if (vpW <= 0 || vpH <= 0)
            return 1.0;

        double fit = Math.Min(vpW / _compareCanvasWidth, vpH / _compareCanvasHeight);
        return Math.Min(fit, 8.0);
    }

    /// <summary>
    /// Applies the fit-to-window zoom to the compare stage, deferring until Avalonia has laid out the
    /// compare ScrollViewers if they're not measured yet (entry to compare mode toggles their
    /// visibility, so Bounds is empty on the same tick).
    /// </summary>
    private void ApplyCompareDefaultZoomWhenReady()
    {
        ScrollViewer probe = ImageCompareWipeScroll.IsVisible ? ImageCompareWipeScroll : ImageCompareSideOriginalScroll;

        if (probe.Bounds.Width <= 0 || probe.Bounds.Height <= 0)
        {
            Dispatcher.UIThread.Post(ApplyCompareDefaultZoomWhenReady, DispatcherPriority.Loaded);
            return;
        }

        SetCompareZoom(ComputeCompareDefaultZoom());
        _compareFitToWindow = true;
    }

    private void ImageCompareScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_compareFitToWindow && ImageComparePanel.IsVisible)
            ApplyCompareDefaultZoomWhenReady();
    }

    private void ImageCompare_PreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _compareFitToWindow = false;
        SetCompareZoom(e.Delta.Y > 0 ? _compareZoom * 1.25 : _compareZoom / 1.25);
        e.Handled = true;
    }

    /// <summary>
    /// Side-by-side scroll sync: when one panel scrolls, mirror the offset onto the other so pixels stay
    /// aligned across the divider. Guarded by <see cref="_syncingCompareScroll"/> so echoing the offset
    /// back doesn't ping-pong forever.
    /// </summary>
    private void ImageCompareSideScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingCompareScroll)
            return;

        ScrollViewer? source = sender as ScrollViewer;
        ScrollViewer? other =
            ReferenceEquals(source, ImageCompareSideOriginalScroll) ? ImageCompareSideModScroll :
            ReferenceEquals(source, ImageCompareSideModScroll) ? ImageCompareSideOriginalScroll : null;
        if (source is null || other is null)
            return;

        _syncingCompareScroll = true;
        try
        {
            if (Math.Abs(other.Offset.X - source.Offset.X) > 0.5 || Math.Abs(other.Offset.Y - source.Offset.Y) > 0.5)
                other.Offset = source.Offset;
        }
        finally
        {
            _syncingCompareScroll = false;
        }
    }

    /// <summary>
    /// Displays a composited XRC scene: the base bitmap fills the Canvas, and each animated overlay
    /// becomes an <see cref="Image"/> at its <c>(x,y)</c>, whose Source is cycled by a shared
    /// <see cref="DispatcherTimer"/> at the fastest overlay's framerate.
    /// </summary>
    private void ShowScene(SceneResource scene)
    {
        StopSceneAnimation();

        SceneBaseImage.Source = scene.Base;
        SceneCanvas.Width = scene.Base.PixelSize.Width;
        SceneCanvas.Height = scene.Base.PixelSize.Height;

        SceneCanvas.Children.Clear();
        SceneCanvas.Children.Add(SceneBaseImage);
        _sceneOverlays.Clear();

        double maxFps = 15.0;
        foreach (SceneAnimatedOverlay overlay in scene.Overlays)
        {
            if (overlay.Frames.Count == 0)
                continue;

            var img = new Image
            {
                Source = overlay.Frames[0],
                Width = overlay.Frames[0].PixelSize.Width,
                Height = overlay.Frames[0].PixelSize.Height,
            };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            Canvas.SetLeft(img, overlay.X);
            Canvas.SetTop(img, overlay.Y);
            SceneCanvas.Children.Add(img);

            _sceneOverlays.Add(new SceneOverlayInstance(img, overlay.Frames));

            if (overlay.Fps > maxFps)
                maxFps = overlay.Fps;
        }

        SceneStatusText.Text = scene.Overlays.Count == 0
            ? $"Scene {(int)SceneCanvas.Width}x{(int)SceneCanvas.Height}"
            : $"Scene {(int)SceneCanvas.Width}x{(int)SceneCanvas.Height} -- {scene.Overlays.Count} animated overlay(s)";

        SetSceneZoom(ComputeSceneDefaultZoom());
        SceneScrollViewer.Offset = new Vector(0, 0);

        if (_sceneOverlays.Count > 0)
        {
            _sceneFrameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, maxFps)) };
            _sceneFrameTimer.Tick += SceneFrameTimer_Tick;
            _sceneFrameTimer.Start();
        }

        ScenePanel.IsVisible = true;
    }

    private void SceneFrameTimer_Tick(object? sender, EventArgs e)
    {
        foreach (SceneOverlayInstance overlay in _sceneOverlays)
        {
            int next = (overlay.FrameIndex + 1) % overlay.Frames.Count;
            overlay.FrameIndex = next;
            overlay.ImageControl.Source = overlay.Frames[next];
        }
    }

    private void StopSceneAnimation()
    {
        if (_sceneFrameTimer is not null)
        {
            _sceneFrameTimer.Stop();
            _sceneFrameTimer.Tick -= SceneFrameTimer_Tick;
            _sceneFrameTimer = null;
        }

        _sceneOverlays.Clear();
    }

    private bool _syncingSceneZoomSlider;

    private void SetSceneZoom(double zoom)
    {
        _sceneZoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        if (!_initialized)
            return;

        _sceneScale.ScaleX = _sceneZoom;
        _sceneScale.ScaleY = _sceneZoom;
        SceneZoomLabel.Text = $"{_sceneZoom * 100:0}%";
        _syncingSceneZoomSlider = true;
        try { SceneZoomSlider.Value = _sceneZoom * 100.0; }
        finally { _syncingSceneZoomSlider = false; }
    }

    private void SceneZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSceneZoomSlider) return;
        SetSceneZoom(e.NewValue / 100.0);
    }

    private void ImageCompareZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingCompareZoomSlider) return;
        _compareFitToWindow = false;
        SetCompareZoom(e.NewValue / 100.0);
    }

    private void SceneDiagnosticsToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (SceneDiagnosticsToggle.IsChecked != true)
        {
            SceneDiagnosticsText.IsVisible = false;
            return;
        }
        if (_vfs is null || _selectedNode is null)
        {
            SceneDiagnosticsToggle.IsChecked = false;
            return;
        }

        // Find the folder's `<name>.xrc` -- same discovery LoadScene uses -- and diagnose it.
        FsNode folder = _selectedNode;
        FsNode? xrc = folder.Children.FirstOrDefault(c =>
            (c.NodeType & FsNodeType.File) != 0 &&
            c.Name.Equals(folder.Name + ".xrc", StringComparison.OrdinalIgnoreCase));
        if (xrc is null)
        {
            SceneDiagnosticsText.Text = "This folder has no scene XRC to diagnose.";
            SceneDiagnosticsText.IsVisible = true;
            return;
        }

        try
        {
            using Stream stream = _vfs.OpenFile(xrc);
            XrcSceneModel.SceneDiagnostics diag = XrcSceneModel.Diagnose(stream);
            SceneDiagnosticsText.Text = FormatSceneDiagnosticsInline(folder.GetPath(), diag);
            SceneDiagnosticsText.IsVisible = true;
        }
        catch (Exception ex)
        {
            SceneDiagnosticsText.Text = $"Diagnostics failed: {ex.Message}";
            SceneDiagnosticsText.IsVisible = true;
        }
    }

    private static string FormatSceneDiagnosticsInline(string scenePath, XrcSceneModel.SceneDiagnostics diag)
    {
        var sb = new StringBuilder();
        sb.AppendLine(scenePath);
        sb.AppendLine();
        sb.AppendLine($"Items: {diag.Items.Count}");
        foreach (XrcSceneModel.ItemDiagnostic row in diag.Items)
        {
            string pos = row.X is int x && row.Y is int y
                ? $"({x},{y})"
                : "(?,?)";
            sb.AppendLine($"  [{row.Index,3}] subT={row.SubType,3} enab={(row.XrcEnabled ? "y" : "n")} pos={pos,-11} \"{row.Name}\"  {row.AssetFile ?? "-"}");
        }
        sb.AppendLine();
        sb.AppendLine($"Enable calls: {diag.ItemEnableCalls.Count}");
        foreach (XrcSceneModel.ItemEnableCall call in diag.ItemEnableCalls)
        {
            sb.AppendLine($"  {call.ScriptName,-20} -> {call.TargetName,-20} enable={call.EnableValue}");
        }
        return sb.ToString();
    }

    private void SceneZoomIn_Click(object? sender, RoutedEventArgs e) => SetSceneZoom(_sceneZoom * 1.25);

    private void SceneZoomOut_Click(object? sender, RoutedEventArgs e) => SetSceneZoom(_sceneZoom / 1.25);

    private void SceneResetZoom_Click(object? sender, RoutedEventArgs e) => SetSceneZoom(ComputeSceneDefaultZoom());

    /// <summary>
    /// Default scene zoom: fit the composed scene inside the ScrollViewer viewport on both axes,
    /// capped at 8x so a small backdrop doesn't blow up to fill the panel. Falls back to 1x when the
    /// viewport hasn't laid out yet or the scene has no size.
    /// </summary>
    private double ComputeSceneDefaultZoom()
    {
        double w = SceneCanvas.Width;
        double h = SceneCanvas.Height;
        double vpW = SceneScrollViewer.Bounds.Width;
        double vpH = SceneScrollViewer.Bounds.Height;
        if (w <= 0 || h <= 0 || vpW <= 0 || vpH <= 0)
            return 1.0;

        double fit = Math.Min(vpW / w, vpH / h);
        return Math.Min(fit, 8.0);
    }

    private void SceneScrollViewer_PreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (SceneBaseImage.Source is null)
            return;

        e.Handled = true;
        SetSceneZoom(e.Delta.Y > 0 ? _sceneZoom * 1.25 : _sceneZoom / 1.25);
    }

    // ---------------------------------------------------------------------
    // 3D model (CIR) resources
    // ---------------------------------------------------------------------

    private void ShowModel(ModelResource model)
    {
        ModelPanel.IsVisible = true;
        ModelCompareToggle.IsVisible = _selectedNode is { HasMod: true };

        try
        {
            LoadModelIntoViewer(model.SourceNode, model.Model);
        }
        catch (Exception ex)
        {
            ModelPanel.IsVisible = false;
            _ = Dialogs.ShowMessageBox(this, $"Failed to display model:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Loads a model's geometry, refreshes the Model/Skin/Animation combos, auto-discovers a same-folder
    /// default animation, and applies a default skin. The default skin is simply whichever entry is first
    /// in the Skin combo; if the combo has no skins the model shows its materials' flat colors.
    /// </summary>
    private void LoadModelIntoViewer(FsNode node, CirModel model)
    {
        // Bumping the generation abandons any in-flight texture load for whatever model was previously showing.
        _modelLoadGeneration++;
        int generation = _modelLoadGeneration;

        _currentModel = model;
        _currentModelNode = node;

        ModelViewerHost.LoadModel(model);
        ModelViewerHost.Loop = ModelLoopCheck.IsChecked == true;

        FsNode? defaultAnimation = FindDefaultAnimation(node);
        _currentAnimationNode = defaultAnimation;

        RefreshModelCombos();

        if (_vfs is not null)
        {
            if (SkinSelectorCombo.SelectedItem is ModelPickItem { Node: { } defaultSkin })
                _ = ApplySkinAsync(defaultSkin, model, node, _vfs, generation);
            else
                ModelViewerHost.ResetMaterialTextures();
        }

        if (defaultAnimation is not null)
        {
            ApplyAnimationSelection(defaultAnimation, autoPlay: true);
            ModelNoAnimationNote.IsVisible = false;
        }
        else
        {
            ModelViewerHost.LoadAnimation(null);
            ModelNoAnimationNote.IsVisible = true;
        }
    }

    /// <summary>Looks for the first <c>.ani</c> file sitting directly next to <paramref name="cirNode"/> (same parent folder). Used to auto-select/auto-play a default animation as soon as a model is opened.</summary>
    private FsNode? FindDefaultAnimation(FsNode cirNode)
    {
        if (_vfs is null || cirNode.Parent is not { } parent)
            return null;

        return _vfs.GetFiles(parent)
            .FirstOrDefault(f => string.Equals(Path.GetExtension(f.Name), ".ani", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // Model / Skin / Animation dropdowns
    //
    // Each combo is scoped to the current model's folder, or to the whole cached ModelBrowseCatalog when
    // "Include whole install" is checked. RefreshModelCombos repopulates ItemsSource/SelectedItem guarded
    // by _updatingModelCombos to suppress the SelectionChanged handlers.
    // ---------------------------------------------------------------------

    private static readonly ModelPickItem NoneItem = new("(none)", null);

    private void ModelIncludeWholeInstall_Changed(object? sender, RoutedEventArgs e) => RefreshModelCombos();

    private void RefreshModelCombos()
    {
        if (_vfs is null || _currentModelNode is null)
            return;

        bool wholeInstall = ModelIncludeWholeInstallCheck.IsChecked == true;

        IEnumerable<FsNode> modelCandidates = wholeInstall
            ? (IEnumerable<FsNode>?)_modelCatalog?.Models ?? []
            : SameFolderFiles(".cir");
        // .tm only; .xmg is for full-screen pictures, not model textures.
        IEnumerable<FsNode> skinCandidates = wholeInstall
            ? (IEnumerable<FsNode>?)_modelCatalog?.Skins ?? []
            : SameFolderFiles(".tm");
        IEnumerable<FsNode> animationCandidates = wholeInstall
            ? (IEnumerable<FsNode>?)_modelCatalog?.Animations ?? []
            : SameFolderFiles(".ani");

        _updatingModelCombos = true;
        try
        {
            List<ModelPickItem> modelItems = BuildItems(modelCandidates, includeNone: false);
            ModelSelectorCombo.ItemsSource = modelItems;
            ModelSelectorCombo.SelectedItem =
                modelItems.FirstOrDefault(i => i.Node == _currentModelNode) ?? modelItems.FirstOrDefault();

            List<ModelPickItem> skinItems = BuildItems(skinCandidates, includeNone: true);
            SkinSelectorCombo.ItemsSource = skinItems;
            // Prefer the first REAL skin over "(none)" so a freshly-loaded model shows textured out of
            // the box. Falls back to "(none)" when no .tm sits next to the model. Skin choice still
            // doesn't persist across model changes on purpose -- each model is reset to its own default.
            ModelPickItem defaultSkinItem = skinItems.FirstOrDefault(i => i.Node is not null) ?? skinItems[0];
            SkinSelectorCombo.SelectedItem = defaultSkinItem;

            List<ModelPickItem> animationItems = BuildItems(animationCandidates, includeNone: true);
            AnimationSelectorCombo.ItemsSource = animationItems;
            AnimationSelectorCombo.SelectedItem =
                animationItems.FirstOrDefault(i => i.Node == _currentAnimationNode) ?? animationItems[0];
        }
        finally
        {
            _updatingModelCombos = false;
        }
    }

    private static List<ModelPickItem> BuildItems(IEnumerable<FsNode> nodes, bool includeNone)
    {
        var items = new List<ModelPickItem>();
        if (includeNone)
            items.Add(NoneItem);

        items.AddRange(nodes
            .OrderBy(n => n.GetPath(), StringComparer.OrdinalIgnoreCase)
            .Select(n => new ModelPickItem(n.GetPath().TrimStart('\\'), n)));

        return items;
    }

    /// <summary>Files with one of <paramref name="extensions"/> living in the same folder as the current model (<see cref="_currentModelNode"/>'s parent). The "same folder" scope used when "Include whole install" is unchecked.</summary>
    private IEnumerable<FsNode> SameFolderFiles(params string[] extensions)
    {
        if (_vfs is null || _currentModelNode?.Parent is not { } parent)
            return [];

        return _vfs.GetFiles(parent)
            .Where(f => extensions.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase));
    }

    private void ModelSelectorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingModelCombos || _vfs is null)
            return;

        if (ModelSelectorCombo.SelectedItem is not ModelPickItem { Node: { } node } || node == _currentModelNode)
            return;

        try
        {
            using Stream stream = _vfs.OpenFile(node);
            CirModel model = CirDecoder.Decode(stream);
            LoadModelIntoViewer(node, model);
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessageBox(this, $"Failed to load model:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnimationSelectorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingModelCombos)
            return;

        if (AnimationSelectorCombo.SelectedItem is not ModelPickItem item)
            return;

        ApplyAnimationSelection(item.Node, autoPlay: true);
        ModelNoAnimationNote.IsVisible = false;
    }

    /// <summary>Decodes (if <paramref name="node"/> is non-null) and loads an animation into the viewer, optionally auto-playing it -- shared by the initial default-animation auto-discovery and the Animation combo's selection handler.</summary>
    private void ApplyAnimationSelection(FsNode? node, bool autoPlay)
    {
        _currentAnimationNode = node;

        // The "Source" button reveals the ani's decoded structure. Hide it when no animation is loaded
        // (nothing to reveal); show it otherwise, regardless of how the model was opened.
        ViewAniSourceButton.IsVisible = node is not null;

        if (node is null || _vfs is null)
        {
            ModelViewerHost.LoadAnimation(null);
            return;
        }

        try
        {
            using Stream stream = _vfs.OpenFile(node);
            AniAnimation animation = AniDecoder.Decode(stream);
            ModelViewerHost.LoadAnimation(animation);
            if (autoPlay)
                ModelViewerHost.Play();
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessageBox(this, $"Failed to load animation:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SkinSelectorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingModelCombos || _vfs is null || _currentModel is null || _currentModelNode is null)
            return;

        if (SkinSelectorCombo.SelectedItem is not ModelPickItem item)
            return;

        _modelLoadGeneration++;
        int generation = _modelLoadGeneration;

        if (item.Node is null)
        {
            // "(none)": revert every material to its flat-colour fallback.
            ModelViewerHost.ResetMaterialTextures();
            return;
        }

        _ = ApplySkinAsync(item.Node, _currentModel, _currentModelNode, _vfs, generation);
    }

    /// <summary>
    /// Applies a skin file to every material of the current model. A material's texture is looked up by
    /// its full unmodified <see cref="CirMaterial.TextureName"/> (case-insensitive) against the skin's
    /// decoded named entries. Materials without a matching entry revert to their flat-colour fallback.
    /// </summary>
    private async Task ApplySkinAsync(FsNode skinNode, CirModel model, FsNode modelNode, VirtualFileSystem vfs, int generation)
    {
        Dictionary<string, DecodedImage>? namedImages = await Task.Run(() => DecodeSkinImages(skinNode, vfs));

        if (generation != _modelLoadGeneration)
            return;

        for (int i = 0; i < model.Materials.Length; i++)
        {
            if (generation != _modelLoadGeneration)
                return;

            int materialIndex = i;
            string textureName = model.Materials[i].TextureName;

            if (namedImages is not null && namedImages.TryGetValue(textureName, out DecodedImage? named))
            {
                ModelViewerHost.SetMaterialTexture(materialIndex, named);
            }
            else
            {
                ModelViewerHost.ResetMaterialTexture(materialIndex);
            }
        }
    }

    /// <summary>Decodes a .tm skin file's named sub-images, or null on decode failure.</summary>
    private static Dictionary<string, DecodedImage>? DecodeSkinImages(FsNode skinNode, VirtualFileSystem vfs)
    {
        try
        {
            using Stream stream = vfs.OpenFile(skinNode);

            IReadOnlyList<TmEntry> entries = TmDecoder.Decode(stream, useMipMap: false);
            var dict = new Dictionary<string, DecodedImage>(StringComparer.OrdinalIgnoreCase);
            foreach (TmEntry entry in entries)
            {
                string name = string.IsNullOrEmpty(entry.Name) ? Path.GetFileNameWithoutExtension(skinNode.Name) : entry.Name;
                dict[name] = entry.Image;
            }

            return dict;
        }
        catch
        {
            // Best-effort: any decode failure means this skin has nothing to offer; caller falls back.
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Animation playback transport
    // ---------------------------------------------------------------------

    private void ModelOrthoView_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        if (!Enum.TryParse<GlModelViewerHost.ViewDirection>(tag, out var direction))
            return;
        ModelViewerHost.SetOrthographicView(direction);
    }

    private void ModelResetView_Click(object? sender, RoutedEventArgs e) => ModelViewerHost.ResetView();

    /// <summary>
    /// Swaps to the text panel showing the currently-selected animation's decoded structure (bone tracks
    /// and keyframes) -- the raw view we used to open by default when a user clicked an .ani. Storing the
    /// owning model node lets <see cref="BackToModel_Click"/> restore the exact model-plus-animation
    /// context the user came from.
    /// </summary>
    private void ViewAniSource_Click(object? sender, RoutedEventArgs e)
    {
        if (_vfs is null || _currentAnimationNode is null || _currentModelNode is null)
            return;

        try
        {
            AniAnimation animation;
            using (Stream s = _vfs.OpenFile(_currentAnimationNode))
                animation = AniDecoder.Decode(s);

            string header =
                $"""
                 ================================================================
                 ANI source (structural decode).
                 Click "Back to Model" above to return to the 3D view.

                 File: {_currentAnimationNode.GetPath()}
                 Model: {_currentModelNode.GetPath()}
                 Duration: {animation.MaxTime} ms
                 Bone tracks: {animation.BoneAnims.Length}
                 ================================================================


                 """;

            _aniSourceReturnAni = _currentAnimationNode;
            _aniSourceReturnModel = _currentModelNode;

            ClearContentPanels();
            TextPanel.Text = header + AniDecoder.DumpAsText(animation);
            TextViewer.IsVisible = true;
            AniSourceHeader.IsVisible = true;
            AniSourceHeaderLabel.Text = $"ANI: {_currentAnimationNode.Name}";
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessageBox(this, $"Could not decode animation source:\n{ex.Message}",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackToModel_Click(object? sender, RoutedEventArgs e)
    {
        if (_aniSourceReturnAni is not null && _aniSourceReturnModel is not null)
        {
            // Restore the model + animation pair we came from. Route through the tree click so the tree
            // selection follows the user, matching what happened before they hit "Source".
            FsNode ani = _aniSourceReturnAni;
            _aniSourceReturnAni = null;
            _aniSourceReturnModel = null;
            LoadSelectedResource(ani);
        }
    }

    // Set by ViewAniSource_Click, cleared by BackToModel_Click; nulled when the user navigates away
    // through the tree, in which case they don't need to be restored.
    private FsNode? _aniSourceReturnAni;
    private FsNode? _aniSourceReturnModel;

    private void ModelCompareToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || !_selectedNode.HasMod)
            return;
        VirtualFileSystem.OpenVariant variant = ModelCompareToggle.IsChecked == true
            ? VirtualFileSystem.OpenVariant.Mod
            : VirtualFileSystem.OpenVariant.Original;
        LoadSelectedResource(_selectedNode, variant);
    }

    private void ModelWireframe_Changed(object? sender, RoutedEventArgs e)
    {
        _settings.ModelViewerWireframe = ModelWireframeCheck.IsChecked == true;
        _settings.Save();
        ModelViewerHost.ShowWireframe = _settings.ModelViewerWireframe;
    }

    private void ModelBackground_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ModelBackgroundCombo.SelectedItem is not ComboBoxItem item)
            return;
        string preset = item.Tag as string ?? "Dark";
        _settings.ModelViewerBackground = preset;
        _settings.Save();
        ApplyModelBackgroundPreset(preset);
    }

    private void ApplyModelBackgroundPreset(string preset)
    {
        (float R, float G, float B, float A) color = preset switch
        {
            "Light" => (0.92f, 0.92f, 0.94f, 1f),
            "Transparent" => (0f, 0f, 0f, 0f),
            _ => (0.16f, 0.16f, 0.18f, 1f), // Dark (default)
        };
        ModelViewerHost.ClearColor = color;
    }

    private async void ModelSavePng_Click(object? sender, RoutedEventArgs e)
    {
        byte[]? pixels = ModelViewerHost.RenderCurrentFrameToBytes(out int w, out int h);
        if (pixels is null)
        {
            await Dialogs.ShowMessageBox(this, "Load a model first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string defaultStem = _currentModelNode is not null
            ? Path.GetFileNameWithoutExtension(_currentModelNode.Name)
            : "screenshot";

        string? path = await Dialogs.ShowSaveFileDialog(
            this, "Save PNG", SanitizeFileName(defaultStem) + ".png",
            [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            _settings.LastExportDir);
        if (path is null)
            return;

        try
        {
            PngWriter.Write(new DecodedImage(w, h, pixels), path);
            RememberExportFolder(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageBox(this, $"Save failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ModelPlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ModelViewerHost.IsPlaying)
        {
            ModelViewerHost.Pause();
        }
        else
        {
            ModelViewerHost.Play();
        }
    }

    private void ModelLoopCheck_Changed(object? sender, RoutedEventArgs e) =>
        ModelViewerHost.Loop = ModelLoopCheck.IsChecked == true;

    private void ModelScrubSlider_PointerPressed(object? sender, PointerPressedEventArgs e) => _modelSliderDragging = true;

    private void ModelScrubSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_modelSliderDragging)
            return;

        ModelViewerHost.SeekToFraction((float)e.NewValue);
    }

    private void ModelScrubSlider_PointerReleased(object? sender, PointerReleasedEventArgs e) => _modelSliderDragging = false;

    private void ModelPlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!ModelPanel.IsVisible)
            return;

        ModelPlayPauseButton.Content = ModelViewerHost.IsPlaying ? "Pause" : "Play";

        if (!_modelSliderDragging)
        {
            ModelScrubSlider.Value = ModelViewerHost.Progress;
        }

        double elapsedMs = ModelViewerHost.Progress * ModelViewerHost.DurationMs;
        double durationMs = ModelViewerHost.DurationMs;

        if (durationMs < 10_000)
        {
            ModelTimeText.Text = $"{elapsedMs:0} ms / {durationMs:0} ms";
        }
        else
        {
            ModelTimeText.Text = $"{elapsedMs / 1000.0:0.0} s / {durationMs / 1000.0:0.0} s";
        }
    }

    private void ImageCompareEnter_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is { HasMod: true } node)
            _ = ShowCompareView(node);
    }

    private void ShowImages(IReadOnlyList<(string Name, DecodedImage Image)> images)
    {
        _currentImages = images.ToList();

        // Mirror the sound/model panels: show the A/B "Compare Mod" affordance whenever the current file
        // has a modded override. Users shouldn't have to right-click the tree to discover the compare
        // view exists.
        ImageCompareEnterButton.IsVisible = _selectedNode is { HasMod: true };

        if (_currentImages.Count > 1)
        {
            ImageSelector.IsVisible = true;
            ImageSelector.ItemsSource = _currentImages.Select((entry, index) =>
                string.IsNullOrEmpty(entry.Name) ? $"[{index}]" : entry.Name).ToList();
            ImageSelector.SelectedIndex = 0;
        }
        else
        {
            ImageSelector.IsVisible = false;
            if (_currentImages.Count == 1)
                SetPreviewImage(_currentImages[0].Image);
        }

        ImagePanel.IsVisible = true;
    }

    private void ImageSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = ImageSelector.SelectedIndex;
        if (index >= 0 && index < _currentImages.Count)
        {
            SetPreviewImage(_currentImages[index].Image);
        }
    }

    private void SetPreviewImage(DecodedImage image)
    {
        _currentDisplayedImage = image;
        PreviewImage.Source = ToBitmap(image);
        ImageScrollViewer.Offset = new Vector(0, 0);
        ApplyDefaultZoom(image);
    }

    private static Bitmap? ToBitmap(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
            return null;

        return PngWriter.ToBitmap(image);
    }

    // ---------------------------------------------------------------------
    // Image zoom & panning
    // ---------------------------------------------------------------------

    private void ApplyDefaultZoom(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            SetZoom(1.0);
            return;
        }

        // "Fit to window, but never more than 8x": compute the largest zoom that keeps the image inside
        // the viewport on both axes, cap at 8x so tiny inventory icons don't blow up to fill the panel.
        // On first load the ScrollViewer hasn't laid out yet (Bounds is empty) -- in that case defer
        // to the next dispatcher tick so we compute against real dimensions rather than falling back
        // to 100% and cropping a large image.
        double vpW = ImageScrollViewer.Bounds.Width;
        double vpH = ImageScrollViewer.Bounds.Height;
        if (vpW <= 0 || vpH <= 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_currentDisplayedImage is { } current && ReferenceEquals(current, image))
                    ApplyDefaultZoom(image);
            }, DispatcherPriority.Loaded);
            return;
        }

        double fit = Math.Min(vpW / image.Width, vpH / image.Height);
        SetZoom(Math.Min(fit, 8.0));
        // Mark AFTER SetZoom, since SetZoom's user-facing callers clear this flag.
        _imageFitToWindow = true;
    }

    private void ImageScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_imageFitToWindow && _currentDisplayedImage is { } image)
            ApplyDefaultZoom(image);
    }

    private bool _syncingImageZoomSlider;

    private void SetZoom(double zoom)
    {
        _imageZoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        // The slider raises ValueChanged during InitializeComponent, before the named XAML fields have
        // been assigned. _initialized flips true only after the constructor finishes; bail out until
        // then. The first real SetZoom call arrives via ApplyDefaultZoom after an image loads.
        if (!_initialized)
            return;

        _imageScale.ScaleX = _imageZoom;
        _imageScale.ScaleY = _imageZoom;
        ZoomLabel.Text = $"{_imageZoom * 100:0}%";
        _syncingImageZoomSlider = true;
        try { ImageZoomSlider.Value = _imageZoom * 100.0; }
        finally { _syncingImageZoomSlider = false; }
    }

    private void ImageZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingImageZoomSlider) return;
        _imageFitToWindow = false;
        SetZoom(e.NewValue / 100.0);
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        _imageFitToWindow = false;
        SetZoom(_imageZoom * 1.25);
    }

    private void ZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        _imageFitToWindow = false;
        SetZoom(_imageZoom / 1.25);
    }

    private void ResetZoom_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentDisplayedImage is { } image)
            ApplyDefaultZoom(image);
    }

    private void ImageScrollViewer_PreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (PreviewImage.Source is null)
            return;

        e.Handled = true;
        _imageFitToWindow = false;
        SetZoom(e.Delta.Y > 0 ? _imageZoom * 1.25 : _imageZoom / 1.25);
    }

    private void ImageScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PreviewImage.Source is null)
            return;
        if (e.GetCurrentPoint(ImageScrollViewer).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        _panStart = e.GetPosition(ImageScrollViewer);
        _panStartHOffset = ImageScrollViewer.Offset.X;
        _panStartVOffset = ImageScrollViewer.Offset.Y;
        e.Pointer.Capture(ImageScrollViewer);
        ImageScrollViewer.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void ImageScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_panStart is not { } start || !e.GetCurrentPoint(ImageScrollViewer).Properties.IsLeftButtonPressed)
            return;

        Point pos = e.GetPosition(ImageScrollViewer);
        ImageScrollViewer.Offset = new Vector(
            _panStartHOffset - (pos.X - start.X),
            _panStartVOffset - (pos.Y - start.Y));
    }

    private void ImageScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panStart = null;
        e.Pointer.Capture(null);
        ImageScrollViewer.Cursor = Cursor.Default;
    }

    // ---------------------------------------------------------------------
    // Sound playback
    // ---------------------------------------------------------------------

    private void SoundCompareToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || !_selectedNode.HasMod)
            return;

        VirtualFileSystem.OpenVariant variant = SoundCompareToggle.IsChecked == true
            ? VirtualFileSystem.OpenVariant.Mod
            : VirtualFileSystem.OpenVariant.Original;
        LoadSelectedResource(_selectedNode, variant);
    }

    private void ShowSound(SoundResource sound)
    {
        string? subtitleText = FormatSubtitle(sound.Subtitle);
        if (subtitleText is not null)
        {
            SubtitleText.Text = subtitleText;
            SubtitleText.IsVisible = true;
        }
        else
        {
            SubtitleText.IsVisible = false;
        }

        SoundSlider.Value = 0;
        // Neutral 0:00 labels until MediaOpened fires with the real duration.
        SoundCurrentTimeText.Text = "0:00";
        SoundTotalTimeText.Text = "0:00";
        SetPlayPauseIcon(playing: false);
        // Show the A/B toggle whenever the current selection has a mod override; keep its state in sync
        // with whichever variant is currently loaded.
        SoundCompareToggle.IsVisible = _selectedNode is { HasMod: true };
        _mediaPlayer.Open(sound.TempFilePath);

        // Render the waveform strip. The audio decode happens off-thread so a big WAV doesn't stall the
        // click; the actual DrawingImage is built on the UI thread. Peaks are cached against the sound
        // instance so window resizes re-render cheaply without re-decoding the file.
        _cachedWaveformPeaks = null;
        _cachedWaveformSource = sound;
        WaveformImage.Source = null;

        string wavPath = sound.TempFilePath;
        _ = Task.Run(() => WaveformRenderer.SamplePeaks(wavPath))
            .ContinueWith(t =>
            {
                if (!ReferenceEquals(_currentContent, sound))
                    return;
                if (t.IsCompletedSuccessfully && t.Result.Length > 0)
                {
                    _cachedWaveformPeaks = t.Result;
                    RenderWaveformAtCurrentWidth();
                }
                else
                {
                    _cachedWaveformPeaks = null;
                    WaveformImage.Source = null;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        SoundPanel.IsVisible = true;

        if (_settings.AutoPlaySound)
            PlaySound();
    }

    /// <summary>
    /// Renders the cached peak buffer at a bar count sized to the panel's current width. Called on
    /// initial peak sample and on every WaveformImage SizeChanged so bar density stays uniform (about one
    /// bar per <see cref="WaveformPixelsPerBar"/> pixels) regardless of window size.
    /// </summary>
    private void RenderWaveformAtCurrentWidth()
    {
        if (_cachedWaveformPeaks is null || _cachedWaveformPeaks.Length == 0)
            return;

        double width = WaveformImage.Bounds.Width;
        if (width < 1)
            width = SoundPanel.Bounds.Width;
        if (width < 1)
            return;

        int barCount = Math.Max(20, (int)Math.Round(width / WaveformPixelsPerBar));

        WaveformImage.Source = WaveformRenderer.Render(
            _cachedWaveformPeaks,
            WaveformColor,
            canvasWidth: width,
            canvasHeight: WaveformImage.Height,
            barCount: barCount);
    }

    private const double WaveformPixelsPerBar = 4.0;
    private static readonly Color WaveformColor = Color.FromRgb(0xE8, 0xEC, 0xF4);

    private void WaveformImage_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
            RenderWaveformAtCurrentWidth();
    }

    /// <summary>Start playback and flip the transport button's icon to Pause.</summary>
    private void PlaySound()
    {
        _mediaPlayer.Play();
        _positionTimer.Start();
        SetPlayPauseIcon(playing: true);
    }

    /// <summary>Pause playback and flip the transport button's icon back to Play.</summary>
    private void PauseSound()
    {
        _mediaPlayer.Pause();
        _positionTimer.Stop();
        SetPlayPauseIcon(playing: false);
    }

    private void SetPlayPauseIcon(bool playing) =>
        PlayPauseIcon.Source = (IImage)Application.Current!.FindResource(playing ? "PauseIcon" : "PlayIcon")!;

    /// <summary>
    /// Formats an ExtendedInfo array into a two-line block for display: speaker name (or clip id) on
    /// the first line, the actual subtitle text below. Returns null when there's nothing meaningful
    /// to show. Matches the shape XrcStructureReader produces for TypeDialogue records:
    /// <c>["[soundName]", "", subtitle]</c>.
    /// </summary>
    private static string? FormatSubtitle(string[]? subtitleLines)
    {
        if (subtitleLines is null || subtitleLines.Length == 0)
            return null;

        // Trim empty leading/trailing lines; collapse multiple blanks between fields to one.
        var nonEmpty = subtitleLines
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();

        return nonEmpty.Length == 0 ? null : string.Join(Environment.NewLine, nonEmpty);
    }

    private void StopSound()
    {
        _positionTimer.Stop();
        _mediaPlayer.Close();
        WaveformImage.Source = null;
    }

    private void MediaPlayer_MediaEnded_SoundLoop(object? sender, EventArgs e)
    {
        if (_settings.LoopSoundPlayback)
        {
            _mediaPlayer.Position = TimeSpan.Zero;
            _mediaPlayer.Play();
        }
        else
        {
            // Rewind to the start so the next Play button click resumes from 0 instead of sitting at the
            // final frame. Also snap the seek slider back and flip the transport icon to Play -- the
            // position timer stopped before it could push the final tick to the slider.
            _positionTimer.Stop();
            _mediaPlayer.Position = TimeSpan.Zero;
            SoundSlider.Value = 0;
            SetPlayPauseIcon(playing: false);
            UpdateSoundTimeReadout();
        }
    }

    private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
    {
        if (_mediaPlayer.HasDurationTimeSpan)
        {
            SoundSlider.Maximum = Math.Max(0.01, _mediaPlayer.Duration!.Value.TotalSeconds);
        }
        UpdateSoundTimeReadout();
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (!_sliderDragging)
        {
            SoundSlider.Value = _mediaPlayer.Position.TotalSeconds;
        }
        UpdateSoundTimeReadout();
    }

    /// <summary>
    /// Refreshes the "M:SS / M:SS" (or "H:MM:SS / H:MM:SS" for hour-plus files) label next to the seek
    /// slider. Called from the position tick during playback and from every state-transition handler
    /// (open, stop, seek, end-of-file) so the label matches the transport even when the timer is idle.
    /// </summary>
    private void UpdateSoundTimeReadout()
    {
        TimeSpan pos = _mediaPlayer.Position;
        TimeSpan total = _mediaPlayer.Duration ?? TimeSpan.Zero;
        SoundCurrentTimeText.Text = FormatSoundTime(pos);
        SoundTotalTimeText.Text = FormatSoundTime(total);
    }

    private static string FormatSoundTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_positionTimer.IsEnabled)
            PauseSound();
        else
            PlaySound();
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
        _positionTimer.Stop();
        SoundSlider.Value = 0;
        SetPlayPauseIcon(playing: false);
        UpdateSoundTimeReadout();
    }

    private void SoundSlider_PointerPressed(object? sender, PointerPressedEventArgs e) => _sliderDragging = true;

    private void SoundSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _sliderDragging = false;
        _mediaPlayer.Position = TimeSpan.FromSeconds(SoundSlider.Value);
        UpdateSoundTimeReadout();
    }

    // ---------------------------------------------------------------------
    // Video: ffmpeg transcodes to MP4 on demand for the LibVLC-backed VideoView
    // ---------------------------------------------------------------------

    private int _videoLoadGeneration;
    private bool _videoIsPlaying;
    private ExternalVideoResource? _currentVideo;

    private void ShowVideo(ExternalVideoResource video)
    {
        _currentVideo = video;
        RemoveBackgroundCheck.IsChecked = _settings.RemoveVideoBackground;
        VideoZoomSlider.Value = 100;
        _ = TranscodeAndPlayAsync(video);
    }

    private async Task TranscodeAndPlayAsync(ExternalVideoResource video)
    {
        VideoPanel.IsVisible = true;
        VideoLabel.Text = $"{video.Kind} video -- transcoding for playback...";
        VideoPlayPauseButton.IsEnabled = false;
        _videoPlayer.Close();

        if (!await EnsureFfmpegAvailable())
        {
            VideoLabel.Text = $"{video.Kind} video -- ffmpeg is required for playback.\n\n" +
                              "Use Options > Locate ffmpeg... to point at a downloaded copy.";
            return;
        }

        int generation = ++_videoLoadGeneration;
        string ffmpegPath = _settings.FfmpegPath;
        string rawPath = video.TempFilePath;
        string? bgColor = _settings.RemoveVideoBackground ? _settings.VideoBackgroundColor : null;
        string overlayColor = _settings.VideoOverlayColor;

        TranscodeResult result = await Task.Run(() => FfmpegTranscoder.TranscodeToMp4(rawPath, ffmpegPath, _tempFiles, bgColor, overlayColor));

        if (generation != _videoLoadGeneration)
            return;

        if (!result.Success)
        {
            VideoLabel.Text = $"{video.Kind} video -- transcoding failed.\n\nffmpeg: {ffmpegPath}\nInput:  {rawPath}\n\n{result.ErrorDetail}";
            return;
        }

        VideoLabel.Text = $"{video.Kind} video";
        _videoPlayer.Open(result.OutputPath!);
        if (_settings.AutoPlayVideo)
        {
            _videoPlayer.Play();
            _videoIsPlaying = true;
            VideoPlayPauseButton.Content = "Pause";
        }
        else
        {
            _videoIsPlaying = false;
            VideoPlayPauseButton.Content = "Play";
        }
        VideoPlayPauseButton.IsEnabled = true;
    }

    private void RemoveBackground_Changed(object? sender, RoutedEventArgs e)
    {
        _settings.RemoveVideoBackground = RemoveBackgroundCheck.IsChecked == true;
        _settings.Save();
        if (_currentVideo is not null)
            _ = TranscodeAndPlayAsync(_currentVideo);
    }

    private void VideoZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        // ValueChanged fires during XAML init before _videoScale/VideoZoomLabel are constructed.
        if (_videoScale is null || VideoZoomLabel is null)
            return;

        double zoom = VideoZoomSlider.Value / 100.0;
        _videoScale.ScaleX = zoom;
        _videoScale.ScaleY = zoom;
        VideoZoomLabel.Text = $"{VideoZoomSlider.Value:0}%";
    }

    private void VideoScrollViewer_PreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        double factor = e.Delta.Y > 0 ? 1.25 : 1.0 / 1.25;
        double newValue = Math.Clamp(VideoZoomSlider.Value * factor, VideoZoomSlider.Minimum, VideoZoomSlider.Maximum);
        VideoZoomSlider.Value = newValue;
        e.Handled = true;
    }

    private void StopVideo()
    {
        _videoLoadGeneration++;
        _videoIsPlaying = false;
        _currentVideo = null;
        _videoPlayer.Close();
    }

    private void VideoPlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentVideo is null)
            return;

        if (_videoIsPlaying)
        {
            _videoPlayer.Pause();
            _videoIsPlaying = false;
            VideoPlayPauseButton.Content = "Play";
        }
        else
        {
            _videoPlayer.Play();
            _videoIsPlaying = true;
            VideoPlayPauseButton.Content = "Pause";
        }
    }

    private void VideoPlayer_MediaEnded(object? sender, EventArgs e)
    {
        // Rewind and pause; user can hit Play to loop.
        _videoPlayer.Position = TimeSpan.Zero;
        _videoPlayer.Pause();
        _videoIsPlaying = false;
        VideoPlayPauseButton.Content = "Play";
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentContent is not ExternalVideoResource video)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(video.TempFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessageBox(this, $"Could not open external player:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
