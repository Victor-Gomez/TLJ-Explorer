using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TLJExplorer.Services;
using TLJExplorer.ViewModels;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Core.Settings;

namespace TLJExplorer;

/// <summary>
/// Interaction logic for MainWindow.xaml. Owns the loaded <see cref="VirtualFileSystem"/>, the currently
/// selected resource, and the sound player. Deliberately a thin code-behind rather than MVVM.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TempFileTracker _tempFiles;
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _positionTimer;

    private readonly DispatcherTimer _searchDebounceTimer;

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
    private sealed class SceneOverlayInstance(
        System.Windows.Controls.Image imageControl,
        IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> frames)
    {
        public System.Windows.Controls.Image ImageControl { get; } = imageControl;
        public IReadOnlyList<System.Windows.Media.Imaging.BitmapSource> Frames { get; } = frames;
        public int FrameIndex { get; set; }
    }

    private readonly List<SceneOverlayInstance> _sceneOverlays = [];

    /// <summary>A single entry in the Model/Skin/Animation dropdowns. <see cref="Node"/> is <see langword="null"/> for the synthetic "(none)" entry offered by the Skin/Animation combos.</summary>
    private sealed record ModelPickItem(string DisplayName, FsNode? Node);

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _tempFiles = ((App)Application.Current).TempFiles;

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

        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded_SoundLoop;

        TypeFilterCombo.ItemsSource = ResourceTypeFilter.Categories;
        TypeFilterCombo.SelectedIndex = 0;

        InitializeOptionsMenu();

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _mediaPlayer.Close();
            StopVideo();
            ModelViewerHost.Dispose();
            _modFolderWatcher?.Dispose();
        };
    }

    /// <summary>
    /// App-wide keyboard shortcuts. Deliberately Preview-level so they fire even when the tree has
    /// keyboard focus, but we bail out when the user is typing in the search box — otherwise "F" would
    /// silently steal the letter, and Space would trigger playback instead of inserting a space.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool typingInSearchBox = SearchBox.IsKeyboardFocused;

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
        else if (!typingInSearchBox && e.Key == Key.Space)
        {
            // Context-aware: whichever panel is visible gets the space bar.
            if (ModelPanel.Visibility == Visibility.Visible && ModelViewerHost.HasAnimation)
            {
                if (ModelViewerHost.IsPlaying) ModelViewerHost.Pause();
                else ModelViewerHost.Play();
                e.Handled = true;
            }
            else if (SoundPanel.Visibility == Visibility.Visible)
            {
                PlayPauseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if (!typingInSearchBox && SoundPanel.Visibility == Visibility.Visible &&
                 (e.Key == Key.Left || e.Key == Key.Right))
        {
            // Arrow-key seek for the sound player: ±5 seconds, clamped to the file's duration.
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                TimeSpan delta = TimeSpan.FromSeconds(e.Key == Key.Right ? 5 : -5);
                TimeSpan target = _mediaPlayer.Position + delta;
                if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                if (target > _mediaPlayer.NaturalDuration.TimeSpan) target = _mediaPlayer.NaturalDuration.TimeSpan;
                _mediaPlayer.Position = target;
                SoundSlider.Value = target.TotalSeconds;
                UpdateSoundTimeReadout();
                e.Handled = true;
            }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.BaseDir))
        {
            await InitVfsAsync(_settings.BaseDir);
        }
        else
        {
            SetStatus("No TLJ install selected. Use File → Select TLJ Install Folder...");
        }
    }

    // ---------------------------------------------------------------------
    // VFS loading
    // ---------------------------------------------------------------------

    private async Task InitVfsAsync(string baseDir)
    {
        ScanProgressBar.Visibility = Visibility.Visible;
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
                path => Dispatcher.BeginInvoke(() => SetStatus($"Scanning: {path}")),
                externalMods));

            _vfs = vfs;
            _vfs.LoadMods = _settings.LoadAssetMods;
            _modelCatalog = null;

            // Tear down any previously-attached watcher (e.g. switching installs) and start a fresh one
            // over this VFS's xarc/ folders.
            _modFolderWatcher?.Dispose();
            _modFolderWatcher = new ModFolderWatcher(vfs, Dispatcher, OnModChanged);

            var rootVm = new FsNodeViewModel(vfs.Root, vfs) { IsExpanded = true };
            Tree.ItemsSource = new[] { rootVm };

            SetStatus($"Loaded \"{baseDir}\".");

            // Restore last selection for this install, if any. Deferred so the tree has been rendered by
            // WPF before we try to walk to and select a specific node.
            if (_settings.LastSelectedPath.TryGetValue(baseDir, out string? savedPath) &&
                !string.IsNullOrEmpty(savedPath))
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    FsNode? target = vfs.FindNode(vfs.Root, savedPath);
                    if (target is not null)
                        TryRestoreTreeSelection(rootVm, target);
                }), System.Windows.Threading.DispatcherPriority.Background);
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
            MessageBox.Show(
                this,
                $"Could not load a TLJ install from:\n{baseDir}\n\n{ex.Message}",
                "TLJ Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetStatus("No TLJ install loaded.");
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
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

    private void TreeFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _ = ApplyTreeFilterAsync();

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
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
        string searchText = SearchBox.Text.Trim();
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
            // itself matched, no subtitle line is highlighted — the user already sees the file name.
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
            // TypeDialogue → ExtendedInfo) also counts as a match. Makes the tree filter usable as a
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

        // Deferred to a lower dispatcher priority so WPF has finished materializing the tree containers
        // before we ask them to bring the target row into view.
        var target_ = cursorVm;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            target_.IsExpanded = target_.IsDirectory && target_.IsExpanded;
            BringVmIntoViewAndSelect(target_);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BringVmIntoViewAndSelect(FsNodeViewModel vm)
    {
        // Set the VM's selection intent by walking the ItemContainerGenerator chain. WPF's TreeView has no
        // clean way to programmatically select a VM in a virtualized/lazy tree; the accepted workaround is
        // to recursively find the TreeViewItem container and set IsSelected/BringIntoView on it.
        var stack = new Stack<FsNodeViewModel>();
        for (var cursor = vm; cursor is not null; )
        {
            stack.Push(cursor);
            cursor = FindParentVm(cursor);
        }

        System.Windows.Controls.ItemsControl parentContainer = Tree;
        while (stack.Count > 0)
        {
            FsNodeViewModel step = stack.Pop();
            if (parentContainer.ItemContainerGenerator.ContainerFromItem(step) is not System.Windows.Controls.TreeViewItem tvi)
                return;

            if (stack.Count == 0)
            {
                tvi.IsSelected = true;
                tvi.BringIntoView();
                return;
            }

            tvi.IsExpanded = true;
            tvi.UpdateLayout();
            parentContainer = tvi;
        }
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
    /// "MOD" pill in the row template updates. Silently no-ops when the VM hasn't been materialized yet —
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

    private FsNodeViewModel? FindParentVm(FsNodeViewModel child)
    {
        if (Tree.ItemsSource is not IEnumerable<FsNodeViewModel> roots)
            return null;
        foreach (FsNodeViewModel root in roots)
        {
            FsNodeViewModel? p = FindParentIn(root, child);
            if (p is not null)
                return p;
        }
        return null;
    }

    private static FsNodeViewModel? FindParentIn(FsNodeViewModel current, FsNodeViewModel target)
    {
        foreach (FsNodeViewModel c in current.Children)
        {
            if (ReferenceEquals(c, target))
                return current;
            FsNodeViewModel? nested = FindParentIn(c, target);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Menu: File
    // ---------------------------------------------------------------------

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select TLJ Install Folder" };
        if (dialog.ShowDialog(this) != true)
            return;

        _settings.BaseDir = dialog.FolderName;
        _settings.RegisterRecentInstall(dialog.FolderName);
        _settings.Save();
        await InitVfsAsync(dialog.FolderName);
        RefreshRecentInstallsMenu();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || _currentContent is null)
        {
            MessageBox.Show(this, "Select a file to export first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        switch (_currentContent)
        {
            case ImageResource { Images.Count: 1 } image:
                ExportSingleImage(image.Images[0]);
                break;

            case ImageResource image:
                ExportMultipleImages(image.Images);
                break;

            case TextResource text:
                ExportText(text.Text, "txt", Path.GetFileNameWithoutExtension(_selectedNode.Name));
                break;

            case SoundResource sound:
                ExportExtractedFile(sound.TempFilePath);
                break;

            case ExternalVideoResource video:
                ExportExtractedFile(video.TempFilePath);
                break;

            case ModelResource model:
                ExportModelAsObj(model);
                break;

            case ErrorResource error:
                MessageBox.Show(this, error.Message, "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void ExportRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || _vfs is null)
        {
            MessageBox.Show(this, "Select a file to export first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TextResource raw = ResourceLoader.LoadRawForced(_selectedNode, _vfs);
            ExportText(raw.Text, "txt", Path.GetFileNameWithoutExtension(_selectedNode.Name) + "_raw");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Raw export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        if (_vfs is null)
        {
            MessageBox.Show(this, "Load a TLJ install first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FsNode? sourceRoot = _selectedNode is { NodeType: var t } && (t & FsNodeType.Directory) != 0
            ? _selectedNode
            : _vfs.Root;

        var dialog = new OpenFolderDialog
        {
            Title = $"Choose output folder for batch export of \"{sourceRoot.GetPath()}\"",
        };
        ApplyRememberedExportFolder(dialog);
        if (dialog.ShowDialog(this) != true)
            return;

        string outputDir = dialog.FolderName;
        RememberExportFolder(outputDir);
        ScanProgressBar.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Minimum = 0;
        ScanProgressBar.Maximum = 1;
        ScanProgressBar.Value = 0;
        CancelBatchButton.Visibility = Visibility.Visible;
        CancelBatchButton.IsEnabled = true;
        SetStatus($"Exporting {sourceRoot.GetPath()} to {outputDir}...");

        VirtualFileSystem vfs = _vfs;
        FsNode sourceNode = sourceRoot;

        _batchExportCts?.Cancel();
        _batchExportCts = new CancellationTokenSource();
        CancellationToken token = _batchExportCts.Token;

        // Progress throttling: a fresh Dispatcher.BeginInvoke per file can flood the UI thread when
        // exporting thousands of assets. Only push a status update every ~40 ms of wall clock.
        DateTime lastUiUpdate = DateTime.MinValue;

        try
        {
            BatchExportSummary summary = await Task.Run(() => BatchExporter.ExportSubtree(
                sourceNode,
                vfs,
                outputDir,
                progress: p =>
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastUiUpdate).TotalMilliseconds < 40 && p.Index != p.Total)
                        return;
                    lastUiUpdate = now;
                    Dispatcher.BeginInvoke(() =>
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
                MessageBox.Show(
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
            MessageBox.Show(this, $"Batch export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Batch export failed.");
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            CancelBatchButton.Visibility = Visibility.Collapsed;
            _batchExportCts?.Dispose();
            _batchExportCts = null;
        }
    }

    private void OpenCommandPalette()
    {
        if (_vfs is null)
            return;

        var palette = new Views.CommandPaletteWindow(_vfs) { Owner = this };
        if (palette.ShowDialog() != true || palette.SelectedNode is null)
            return;

        // Selecting through the tree ensures a11y focus, selection preservation, and status-bar update all
        // work exactly the same as a manual click.
        FsNodeViewModel? rootVm = (Tree.ItemsSource as IEnumerable<FsNodeViewModel>)?.FirstOrDefault();
        if (rootVm is not null)
            TryRestoreTreeSelection(rootVm, palette.SelectedNode);
        else
            LoadSelectedResource(palette.SelectedNode);
    }

    private async void ExportSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_vfs is null)
        {
            MessageBox.Show(this, "Load a TLJ install first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose output folder for subtitle export" };
        ApplyRememberedExportFolder(dialog);
        if (dialog.ShowDialog(this) != true)
            return;

        string outputDir = dialog.FolderName;
        RememberExportFolder(outputDir);

        ScanProgressBar.Visibility = Visibility.Visible;
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
            MessageBox.Show(this,
                $"Exported {lineCount} dialogue line(s) across {sceneCount} scene(s):\n\n" +
                $"  {Path.Combine(outputDir, "dialogue.csv")}\n" +
                $"  {Path.Combine(outputDir, "srt")}\\*.srt",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Subtitle export failed.");
            MessageBox.Show(this, $"Subtitle export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            ScanProgressBar.IsIndeterminate = false;
        }
    }

    private void CancelBatchButton_Click(object sender, RoutedEventArgs e)
    {
        _batchExportCts?.Cancel();
        CancelBatchButton.IsEnabled = false;
    }

    /// <summary>
    /// Resolves the tree row a context-menu click came from. WPF plumbs the row's DataContext through the
    /// MenuItem's PlacementTarget-chain — walk it back to the <see cref="FsNodeViewModel"/> so the handler
    /// doesn't rely on TreeView.SelectedItem (which the right-click may not have updated).
    /// </summary>
    private static FsNode? ResolveContextTarget(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem menuItem)
            return null;

        DependencyObject? placement = null;
        for (DependencyObject? cursor = menuItem; cursor is not null; cursor = LogicalTreeHelper.GetParent(cursor))
        {
            if (cursor is System.Windows.Controls.ContextMenu cm)
            {
                placement = cm.PlacementTarget;
                break;
            }
        }

        for (DependencyObject? cursor = placement; cursor is not null; cursor = VisualTreeHelper.GetParent(cursor))
        {
            if (cursor is System.Windows.Controls.TreeViewItem tvi && tvi.DataContext is FsNodeViewModel vm)
                return vm.Node;
        }

        return null;
    }

    private void TreeContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null)
            return;
        try
        {
            Clipboard.SetText(node.GetPath());
            SetStatus($"Copied path: {node.GetPath()}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not copy to clipboard:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TreeContextRevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null)
            return;

        // Only loose files (not in-archive) live on disk at a real path we can hand to Explorer.
        if ((node.NodeType & FsNodeType.InArchive) != 0 || string.IsNullOrEmpty(node.ArchivePath))
        {
            MessageBox.Show(this,
                "This entry lives inside a .xarc archive, not as a file on disk — Explorer can't reveal it. Use Export instead.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.ArchivePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open Explorer:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TreeContextExportItem_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || (node.NodeType & FsNodeType.File) == 0)
        {
            MessageBox.Show(this, "Select a file to export.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Select the target node first so the existing Export_Click flow (which uses _selectedNode /
        // _currentContent) has the right state, then delegate.
        LoadSelectedResource(node);
        Export_Click(this, new RoutedEventArgs());
    }

    private void TreeContextViewOriginal_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || (node.NodeType & FsNodeType.File) == 0 || _vfs is null)
            return;

        if (!node.HasMod)
        {
            MessageBox.Show(this, "This entry has no mod override.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Force-load the archive variant regardless of the LoadMods toggle. The one-shot bypass leaves the
        // global setting alone.
        LoadSelectedResource(node, VirtualFileSystem.OpenVariant.Original);
    }

    private void TreeContextCompareMod_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || (node.NodeType & FsNodeType.File) == 0 || _vfs is null)
            return;

        if (!node.HasMod)
        {
            MessageBox.Show(this, "This entry has no mod override to compare against.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowCompareView(node);
    }

    private void TreeContextExtractAsMod_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null || _vfs is null || (node.NodeType & FsNodeType.File) == 0)
            return;

        // Only archive entries need a "mod extract" — loose files are already editable in place. For
        // clarity, refuse instead of silently doing nothing when the entry is already loose.
        if ((node.NodeType & FsNodeType.InArchive) == 0)
        {
            MessageBox.Show(this,
                "This entry is already a loose file on disk — open it in Windows Explorer instead of extracting.",
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
            MessageBoxResult overwrite = MessageBox.Show(this,
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
            MessageBox.Show(this, $"Extract failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TreeContextPlayLocalized_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show(this,
                node.IsLocalized
                    ? "No English (flag=0) sibling found for this entry."
                    : "No localised (flag=1) sibling found for this entry.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoadSelectedResource(sibling);
    }

    private async void TreeContextBatchExport_Click(object sender, RoutedEventArgs e)
    {
        FsNode? node = ResolveContextTarget(sender);
        if (node is null)
            return;

        // If the user right-clicked a file, batch-export its parent folder — that matches Explorer's
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

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ExportModelAsObj(ModelResource model)
    {
        string defaultStem = _selectedNode is not null
            ? Path.GetFileNameWithoutExtension(_selectedNode.Name)
            : "model";

        var dialog = new SaveFileDialog
        {
            Title = "Export Model",
            Filter = "Binary glTF (*.glb)|*.glb|Wavefront OBJ (*.obj)|*.obj",
            FilterIndex = 1,
            FileName = SanitizeFileName(defaultStem) + ".glb",
        };
        ApplyRememberedExportFolder(dialog);

        if (dialog.ShowDialog(this) != true)
            return;

        AniAnimation? bindPose = TryLoadCurrentBindPose();

        try
        {
            string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (ext == ".obj")
            {
                ObjWriter.Write(model.Model, dialog.FileName, bindPose);
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
                GlbWriter.Write(model.Model, dialog.FileName, options);
            }
            RememberExportFolder(dialog.FileName);

            if (bindPose is null && model.Model.Skeleton.Length > 1)
            {
                MessageBox.Show(this,
                    "Exported without a bind-pose animation. The mesh will look like a jumble of bone-local fragments in Blender.\n\n" +
                    "Select an animation (Animation dropdown, or enable 'Include whole install') before exporting to get a properly-posed and skinned model.",
                    "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
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
            ModelNoAnimationNote.Visibility = Visibility.Collapsed;

            SetStatus($"{aniNode.GetPath()}  →  {cirNode.Name}  (animation applied)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open model for animation:\n{ex.Message}",
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

    private void ExportSingleImage((string Name, DecodedImage Image) entry)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Image",
            Filter = "Targa image (*.tga)|*.tga",
            FileName = SanitizeFileName(entry.Name) + ".tga",
        };
        ApplyRememberedExportFolder(dialog);

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            TgaWriter.Write(entry.Image, dialog.FileName);
            RememberExportFolder(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportMultipleImages(IReadOnlyList<(string Name, DecodedImage Image)> images)
    {
        var dialog = new OpenFolderDialog { Title = "Select destination folder for exported images" };
        ApplyRememberedExportFolder(dialog);
        if (dialog.ShowDialog(this) != true)
            return;

        RememberExportFolder(dialog.FolderName);

        int exported = 0;
        try
        {
            foreach (var (name, image) in images)
            {
                string fileName = SanitizeFileName(string.IsNullOrEmpty(name) ? $"image_{exported}" : name) + ".tga";
                string path = Path.Combine(dialog.FolderName, fileName);
                TgaWriter.Write(image, path);
                exported++;
            }

            MessageBox.Show(this, $"Exported {exported} image(s) to:\n{dialog.FolderName}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed after {exported} image(s):\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportText(string text, string extension, string defaultNameWithoutExtension)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Text",
            Filter = $"Text file (*.{extension})|*.{extension}",
            FileName = SanitizeFileName(defaultNameWithoutExtension) + "." + extension,
        };
        ApplyRememberedExportFolder(dialog);

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, text);
            RememberExportFolder(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportExtractedFile(string tempFilePath)
    {
        string extension = Path.GetExtension(tempFilePath).TrimStart('.');
        var dialog = new SaveFileDialog
        {
            Title = "Export",
            Filter = $"{extension.ToUpperInvariant()} file (*.{extension})|*.{extension}",
            FileName = (_selectedNode is not null ? Path.GetFileNameWithoutExtension(_selectedNode.Name) : "export") + "." + extension,
        };
        ApplyRememberedExportFolder(dialog);

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.Copy(tempFilePath, dialog.FileName, overwrite: true);
            RememberExportFolder(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Points a Save-File / Open-Folder dialog at <see cref="AppSettings.LastExportDir"/> so consecutive
    /// exports don't reset the user to Documents each time. Silently no-ops when the remembered folder no
    /// longer exists (drive removed, user deleted it, etc.).
    /// </summary>
    private void ApplyRememberedExportFolder(FileDialog dialog)
    {
        if (!string.IsNullOrEmpty(_settings.LastExportDir) && Directory.Exists(_settings.LastExportDir))
            dialog.InitialDirectory = _settings.LastExportDir;
    }

    private void ApplyRememberedExportFolder(OpenFolderDialog dialog)
    {
        if (!string.IsNullOrEmpty(_settings.LastExportDir) && Directory.Exists(_settings.LastExportDir))
            dialog.InitialDirectory = _settings.LastExportDir;
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
        ShowMipMapsMenuItem.IsChecked = _settings.ShowMipMaps;
        HighQualityMenuItem.IsChecked = _settings.HighQuality;
        DumpSceneDiagnosticsMenuItem.IsChecked = _settings.DumpSceneDiagnostics;
        HideLocalizedMenuItem.IsChecked = _settings.HideLocalizedEntries;
        LoadAssetModsMenuItem.IsChecked = _settings.LoadAssetMods;
        UpdateExternalModsMenuState();
        UpdateAntiAliasChecks();
        ApplyTheme(_settings.Theme);
    }

    private void ApplyTheme(string theme)
    {
        // WPF's ThemeMode API is currently marked "for evaluation purposes only" and raises WPF0001.
        // Suppress here rather than project-wide -- if the API changes we want the warning re-surfacing.
#pragma warning disable WPF0001
        ThemeMode mode = theme switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark,
        };
        Application.Current.ThemeMode = mode;
        this.ThemeMode = mode;

        ThemeSystemMenuItem.IsChecked = mode == ThemeMode.System;
        ThemeLightMenuItem.IsChecked = mode == ThemeMode.Light;
        ThemeDarkMenuItem.IsChecked = mode == ThemeMode.Dark;
#pragma warning restore WPF0001
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string theme })
        {
            _settings.Theme = theme;
            _settings.Save();
            ApplyTheme(theme);
        }
    }

    private void UpdateAntiAliasChecks()
    {
        AntiAliasNoneMenuItem.IsChecked = _settings.AntiAliasSamples == 0;
        AntiAlias2xMenuItem.IsChecked = _settings.AntiAliasSamples == 2;
        AntiAlias4xMenuItem.IsChecked = _settings.AntiAliasSamples == 4;
        AntiAliasDefaultMenuItem.IsChecked = _settings.AntiAliasSamples == -1;
    }

    private void AutoPlaySound_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoPlaySound = AutoPlaySoundMenuItem.IsChecked;
        _settings.Save();
    }

    private void AutoPlayVideo_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoPlayVideo = AutoPlayVideoMenuItem.IsChecked;
        _settings.Save();
    }

    private void LoopSound_Click(object sender, RoutedEventArgs e)
    {
        _settings.LoopSoundPlayback = LoopSoundMenuItem.IsChecked;
        _settings.Save();
    }

    private void RefreshRecentInstallsMenu()
    {
        RecentInstallsMenu.ItemsSource = _settings.RecentInstalls;
        RecentInstallsMenu.IsEnabled = _settings.RecentInstalls.Count > 0;
    }

    private async void RecentInstallItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string path })
            return;
        if (!Directory.Exists(path))
        {
            _settings.RecentInstalls.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            RefreshRecentInstallsMenu();
            MessageBox.Show(this, $"Install folder no longer exists:\n{path}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.BaseDir = path;
        _settings.RegisterRecentInstall(path);
        _settings.Save();
        await InitVfsAsync(path);
        RefreshRecentInstallsMenu();
    }

    private void ShowMipMaps_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowMipMaps = ShowMipMapsMenuItem.IsChecked;
        _settings.Save();

        // Affects .tm decoding directly: re-decode the current selection if it's still a TM texture.
        if (_selectedNode is not null && string.Equals(Path.GetExtension(_selectedNode.Name), ".tm", StringComparison.OrdinalIgnoreCase))
        {
            LoadSelectedResource(_selectedNode);
        }
    }

    private void AntiAlias_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string tagValue } && int.TryParse(tagValue, out int samples))
        {
            _settings.AntiAliasSamples = samples;
            _settings.Save();
            UpdateAntiAliasChecks();
        }
    }

    private void HighQuality_Click(object sender, RoutedEventArgs e)
    {
        _settings.HighQuality = HighQualityMenuItem.IsChecked;
        _settings.Save();
    }

    private void DumpSceneDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _settings.DumpSceneDiagnostics = DumpSceneDiagnosticsMenuItem.IsChecked;
        _settings.Save();
    }

    private void HideLocalized_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideLocalizedEntries = HideLocalizedMenuItem.IsChecked;
        _settings.Save();
        _ = ApplyTreeFilterAsync();
    }

    private void LoadAssetMods_Click(object sender, RoutedEventArgs e)
    {
        _settings.LoadAssetMods = LoadAssetModsMenuItem.IsChecked;
        _settings.Save();
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

    private async void SelectExternalModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select External Mods Folder" };
        if (!string.IsNullOrEmpty(_settings.ExternalModsDir) && Directory.Exists(_settings.ExternalModsDir))
            dialog.InitialDirectory = _settings.ExternalModsDir;

        if (dialog.ShowDialog(this) != true)
            return;

        _settings.ExternalModsDir = dialog.FolderName;
        _settings.Save();
        UpdateExternalModsMenuState();

        if (!string.IsNullOrEmpty(_settings.BaseDir))
            await InitVfsAsync(_settings.BaseDir);
    }

    private async void ClearExternalModsFolder_Click(object sender, RoutedEventArgs e)
    {
        _settings.ExternalModsDir = null;
        _settings.Save();
        UpdateExternalModsMenuState();

        if (!string.IsNullOrEmpty(_settings.BaseDir))
            await InitVfsAsync(_settings.BaseDir);
    }

    private void UpdateExternalModsMenuState() =>
        ClearExternalModsMenuItem.IsEnabled = !string.IsNullOrEmpty(_settings.ExternalModsDir);

    private void DiagnoseExternalMods_Click(object sender, RoutedEventArgs e)
    {
        if (_vfs is null)
        {
            MessageBox.Show(this, "Load a TLJ install first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string report = ExternalModsDiagnostic.Build(_vfs);

        ClearContentPanels();
        TextPanel.Text = report;
        TextViewer.Visibility = Visibility.Visible;
        SetStatus("External mods diagnostic report shown in the viewer.");
    }

    // ---------------------------------------------------------------------
    // Tree selection
    // ---------------------------------------------------------------------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FsNodeViewModel { IsPlaceholder: false } vm)
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
        ScenePanel.Visibility = Visibility.Visible;
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

    private void LoadSelectedResource(FsNode node, VirtualFileSystem.OpenVariant variant = VirtualFileSystem.OpenVariant.Preferred)
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

        ResourceContent content = ResourceLoader.Load(node, _vfs, _settings, _tempFiles, variant);
        _currentContent = content;
        ShowContent(content);

        string variantSuffix = variant switch
        {
            VirtualFileSystem.OpenVariant.Original when node.HasMod => "  [original]",
            VirtualFileSystem.OpenVariant.Mod when node.HasMod => "  [modded]",
            _ => node.HasMod && _vfs.LoadMods ? "  [modded]" : "",
        };
        SetStatus($"{node.GetPath()}  —  {FormatContentMetadata(node, content)}{variantSuffix}");
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
                $"{single.Images[0].Image.Width}×{single.Images[0].Image.Height}",
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
        ImagePanel.Visibility = Visibility.Collapsed;
        TextViewer.Visibility = Visibility.Collapsed;
        AniSourceHeader.Visibility = Visibility.Collapsed;
        SoundPanel.Visibility = Visibility.Collapsed;
        VideoPanel.Visibility = Visibility.Collapsed;
        ModelPanel.Visibility = Visibility.Collapsed;
        ScenePanel.Visibility = Visibility.Collapsed;
        ImageComparePanel.Visibility = Visibility.Collapsed;

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
    private Point? _panStart;
    private double _panStartHOffset;
    private double _panStartVOffset;

    private const double MinZoom = 0.25;
    private const double MaxZoom = 16.0;
    private const double PreferredDisplaySize = 320.0;

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
                TextViewer.Visibility = Visibility.Visible;
                break;

            case SoundResource sound:
                ShowSound(sound);
                break;

            case ExternalVideoResource video:
                ShowVideo(video);
                break;

            case ErrorResource error:
                TextPanel.Text = error.Message;
                TextViewer.Visibility = Visibility.Visible;
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
    private void ShowCompareView(FsNode node)
    {
        if (_vfs is null)
            return;

        string ext = Path.GetExtension(node.Name).ToLowerInvariant();
        if (ext is not (".xmg" or ".tm"))
        {
            MessageBox.Show(this,
                "The vertical-wipe compare view currently supports images (.xmg, .tm) only. Use the A/B toggle in the sound/model panels for other asset kinds.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DecodedImage? original = TryDecodeSingleImage(node, VirtualFileSystem.OpenVariant.Original, ext);
        DecodedImage? modded = TryDecodeSingleImage(node, VirtualFileSystem.OpenVariant.Mod, ext);
        if (original is null || modded is null)
        {
            MessageBox.Show(this, "Could not decode both variants for comparison.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private BitmapSource? _compareOriginalBitmap;
    private BitmapSource? _compareModBitmap;
    private double _compareWipeX;
    private double _compareZoom = 1.0;
    private int _compareCanvasWidth;
    private int _compareCanvasHeight;
    private bool _syncingCompareScroll;

    private void ShowImageCompare(ImageCompareResource compare)
    {
        _compareOriginalBitmap = ToBitmapSource(compare.Original);
        _compareModBitmap = ToBitmapSource(compare.Modded);

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
        // Setting the slider raises ValueChanged which calls UpdateCompareClip; force a value that fires
        // even when it happens to already be 0.5 by cycling once through a bracket.
        ImageCompareWipeSlider.Value = 0.5;
        SetCompareZoom(1.0);
        UpdateCompareClip();

        ImageCompareLabel.Text = $"Comparing {compare.OriginalLabel} — original {compare.Original.Width}×{compare.Original.Height}, " +
                                 $"mod {compare.Modded.Width}×{compare.Modded.Height}, canvas {_compareCanvasWidth}×{_compareCanvasHeight}";

        ImageComparePanel.Visibility = Visibility.Visible;
        ApplyImageCompareMode();
    }

    private void UpdateCompareClip()
    {
        if (_compareOriginalBitmap is null)
            return;
        double x = Math.Max(0, Math.Min(_compareCanvasWidth, _compareWipeX));
        // Dual clip: ORIGINAL renders only on the LEFT of the divider, MOD only on the RIGHT. Because
        // neither image draws over the other's half, transparent pixels don't reveal a stretched low-res
        // version of the other side.
        ImageCompareOriginalClip.Rect = new Rect(0, 0, x, _compareCanvasHeight);
        ImageCompareClip.Rect = new Rect(x, 0, _compareCanvasWidth - x, _compareCanvasHeight);
        ImageCompareDivider.Margin = new Thickness(x - 1, 0, 0, 0);
        ImageCompareDivider.Height = _compareCanvasHeight;
    }

    private void ApplyImageCompareMode()
    {
        bool wipe = ImageCompareWipeMode.IsChecked == true;
        ImageCompareWipeScroll.Visibility = wipe ? Visibility.Visible : Visibility.Collapsed;
        ImageCompareSideBySideContainer.Visibility = wipe ? Visibility.Collapsed : Visibility.Visible;
        // The wipe slider only makes sense in wipe mode — hide it in side-by-side.
        ImageCompareWipeSliderPanel.Visibility = wipe ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ImageCompareMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ImageComparePanel.Visibility == Visibility.Visible)
            ApplyImageCompareMode();
    }

    private void ImageCompareWipeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_compareCanvasWidth <= 0)
            return;
        _compareWipeX = e.NewValue * _compareCanvasWidth;
        UpdateCompareClip();
    }

    // -------------------- Pan (left-drag on any compare ScrollViewer) --------------------

    private Point? _comparePanStart;
    private double _comparePanStartH;
    private double _comparePanStartV;
    private System.Windows.Controls.ScrollViewer? _comparePanTarget;

    private void ImageComparePanScroll_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv)
            return;
        _comparePanTarget = sv;
        _comparePanStart = e.GetPosition(sv);
        _comparePanStartH = sv.HorizontalOffset;
        _comparePanStartV = sv.VerticalOffset;
        sv.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void ImageComparePanScroll_MouseMove(object sender, MouseEventArgs e)
    {
        if (_comparePanStart is not { } start || _comparePanTarget is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point p = e.GetPosition(_comparePanTarget);
        _comparePanTarget.ScrollToHorizontalOffset(_comparePanStartH - (p.X - start.X));
        _comparePanTarget.ScrollToVerticalOffset(_comparePanStartV - (p.Y - start.Y));
    }

    private void ImageComparePanScroll_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _comparePanStart = null;
        _comparePanTarget?.ReleaseMouseCapture();
        _comparePanTarget = null;
        Mouse.OverrideCursor = null;
    }

    private void ImageCompareClose_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is not null)
            LoadSelectedResource(_selectedNode);
    }

    // ---------------- Zoom & pan (shared between wipe and side-by-side) ----------------

    private const double CompareMinZoom = 0.1;
    private const double CompareMaxZoom = 16.0;

    private void SetCompareZoom(double zoom)
    {
        _compareZoom = Math.Clamp(zoom, CompareMinZoom, CompareMaxZoom);
        ImageCompareStageScale.ScaleX = _compareZoom;
        ImageCompareStageScale.ScaleY = _compareZoom;
        ImageCompareSideOriginalScale.ScaleX = _compareZoom;
        ImageCompareSideOriginalScale.ScaleY = _compareZoom;
        ImageCompareSideModScale.ScaleX = _compareZoom;
        ImageCompareSideModScale.ScaleY = _compareZoom;
        ImageCompareZoomLabel.Text = $"{_compareZoom * 100:0}%";
    }

    private void ImageCompareZoomIn_Click(object sender, RoutedEventArgs e) => SetCompareZoom(_compareZoom * 1.25);
    private void ImageCompareZoomOut_Click(object sender, RoutedEventArgs e) => SetCompareZoom(_compareZoom / 1.25);
    private void ImageCompareZoomReset_Click(object sender, RoutedEventArgs e) => SetCompareZoom(1.0);

    private void ImageCompare_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetCompareZoom(e.Delta > 0 ? _compareZoom * 1.25 : _compareZoom / 1.25);
        e.Handled = true;
    }

    /// <summary>
    /// Side-by-side scroll sync: when one panel scrolls, mirror the offset onto the other so pixels stay
    /// aligned across the divider. Guarded by <see cref="_syncingCompareScroll"/> so echoing the offset
    /// back doesn't ping-pong forever.
    /// </summary>
    private void ImageCompareSideScroll_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_syncingCompareScroll)
            return;

        System.Windows.Controls.ScrollViewer? source = sender as System.Windows.Controls.ScrollViewer;
        System.Windows.Controls.ScrollViewer? other =
            ReferenceEquals(source, ImageCompareSideOriginalScroll) ? ImageCompareSideModScroll :
            ReferenceEquals(source, ImageCompareSideModScroll) ? ImageCompareSideOriginalScroll : null;
        if (source is null || other is null)
            return;

        _syncingCompareScroll = true;
        try
        {
            if (Math.Abs(other.HorizontalOffset - source.HorizontalOffset) > 0.5)
                other.ScrollToHorizontalOffset(source.HorizontalOffset);
            if (Math.Abs(other.VerticalOffset - source.VerticalOffset) > 0.5)
                other.ScrollToVerticalOffset(source.VerticalOffset);
        }
        finally
        {
            _syncingCompareScroll = false;
        }
    }

    /// <summary>
    /// Displays a composited XRC scene: the base bitmap fills the Canvas, and each animated overlay
    /// becomes an <see cref="System.Windows.Controls.Image"/> at its <c>(x,y)</c>, whose Source is cycled
    /// by a shared <see cref="DispatcherTimer"/> at the fastest overlay's framerate.
    /// </summary>
    private void ShowScene(SceneResource scene)
    {
        StopSceneAnimation();

        SceneBaseImage.Source = scene.Base;
        SceneCanvas.Width = scene.Base.PixelWidth;
        SceneCanvas.Height = scene.Base.PixelHeight;

        SceneCanvas.Children.Clear();
        SceneCanvas.Children.Add(SceneBaseImage);
        _sceneOverlays.Clear();

        double maxFps = 15.0;
        foreach (SceneAnimatedOverlay overlay in scene.Overlays)
        {
            if (overlay.Frames.Count == 0)
                continue;

            var img = new System.Windows.Controls.Image
            {
                Source = overlay.Frames[0],
                Width = overlay.Frames[0].PixelWidth,
                Height = overlay.Frames[0].PixelHeight,
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
            System.Windows.Controls.Canvas.SetLeft(img, overlay.X);
            System.Windows.Controls.Canvas.SetTop(img, overlay.Y);
            SceneCanvas.Children.Add(img);

            _sceneOverlays.Add(new SceneOverlayInstance(img, overlay.Frames));

            if (overlay.Fps > maxFps)
                maxFps = overlay.Fps;
        }

        SceneStatusText.Text = scene.Overlays.Count == 0
            ? $"Scene {(int)SceneCanvas.Width}x{(int)SceneCanvas.Height}"
            : $"Scene {(int)SceneCanvas.Width}x{(int)SceneCanvas.Height} -- {scene.Overlays.Count} animated overlay(s)";

        SetSceneZoom(1.0);
        SceneScrollViewer.ScrollToHorizontalOffset(0);
        SceneScrollViewer.ScrollToVerticalOffset(0);

        if (_sceneOverlays.Count > 0)
        {
            _sceneFrameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, maxFps)) };
            _sceneFrameTimer.Tick += SceneFrameTimer_Tick;
            _sceneFrameTimer.Start();
        }

        ScenePanel.Visibility = Visibility.Visible;
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

    private void SetSceneZoom(double zoom)
    {
        _sceneZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        SceneScale.ScaleX = _sceneZoom;
        SceneScale.ScaleY = _sceneZoom;
        SceneZoomLabel.Text = $"{_sceneZoom * 100:0}%";
    }

    private void SceneDiagnosticsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (SceneDiagnosticsToggle.IsChecked != true)
        {
            SceneDiagnosticsText.Visibility = Visibility.Collapsed;
            return;
        }
        if (_vfs is null || _selectedNode is null)
        {
            SceneDiagnosticsToggle.IsChecked = false;
            return;
        }

        // Find the folder's `<name>.xrc` — same discovery LoadScene uses — and diagnose it.
        FsNode folder = _selectedNode;
        FsNode? xrc = folder.Children.FirstOrDefault(c =>
            (c.NodeType & FsNodeType.File) != 0 &&
            c.Name.Equals(folder.Name + ".xrc", StringComparison.OrdinalIgnoreCase));
        if (xrc is null)
        {
            SceneDiagnosticsText.Text = "This folder has no scene XRC to diagnose.";
            SceneDiagnosticsText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using Stream stream = _vfs.OpenFile(xrc);
            XrcSceneModel.SceneDiagnostics diag = XrcSceneModel.Diagnose(stream);
            SceneDiagnosticsText.Text = FormatSceneDiagnosticsInline(folder.GetPath(), diag);
            SceneDiagnosticsText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SceneDiagnosticsText.Text = $"Diagnostics failed: {ex.Message}";
            SceneDiagnosticsText.Visibility = Visibility.Visible;
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
            sb.AppendLine($"  {call.ScriptName,-20} → {call.TargetName,-20} enable={call.EnableValue}");
        }
        return sb.ToString();
    }

    private void SceneZoomIn_Click(object sender, RoutedEventArgs e) => SetSceneZoom(_sceneZoom * 1.25);

    private void SceneZoomOut_Click(object sender, RoutedEventArgs e) => SetSceneZoom(_sceneZoom / 1.25);

    private void SceneResetZoom_Click(object sender, RoutedEventArgs e) => SetSceneZoom(1.0);

    private void SceneScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SceneBaseImage.Source is null)
            return;

        e.Handled = true;
        SetSceneZoom(e.Delta > 0 ? _sceneZoom * 1.25 : _sceneZoom / 1.25);
    }

    // ---------------------------------------------------------------------
    // 3D model (CIR) resources
    // ---------------------------------------------------------------------

    private void ShowModel(ModelResource model)
    {
        ModelPanel.Visibility = Visibility.Visible;
        ModelCompareToggle.Visibility = _selectedNode is { HasMod: true } ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            LoadModelIntoViewer(model.SourceNode, model.Model);
        }
        catch (Exception ex)
        {
            ModelPanel.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, $"Failed to display model:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
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
            ModelNoAnimationNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelViewerHost.LoadAnimation(null);
            ModelNoAnimationNote.Visibility = Visibility.Visible;
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

    private void ModelIncludeWholeInstall_Changed(object sender, RoutedEventArgs e) => RefreshModelCombos();

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
            // doesn't persist across model changes on purpose — each model is reset to its own default.
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

    private void ModelSelectorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
            MessageBox.Show(this, $"Failed to load model:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnimationSelectorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingModelCombos)
            return;

        if (AnimationSelectorCombo.SelectedItem is not ModelPickItem item)
            return;

        ApplyAnimationSelection(item.Node, autoPlay: true);
        ModelNoAnimationNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>Decodes (if <paramref name="node"/> is non-null) and loads an animation into the viewer, optionally auto-playing it -- shared by the initial default-animation auto-discovery and the Animation combo's selection handler.</summary>
    private void ApplyAnimationSelection(FsNode? node, bool autoPlay)
    {
        _currentAnimationNode = node;

        // The "Source" button reveals the ani's decoded structure. Hide it when no animation is loaded
        // (nothing to reveal); show it otherwise, regardless of how the model was opened.
        ViewAniSourceButton.Visibility = node is not null ? Visibility.Visible : Visibility.Collapsed;

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
            MessageBox.Show(this, $"Failed to load animation:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SkinSelectorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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

    private void ModelOrthoView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag })
            return;
        if (!Enum.TryParse<Views.GlModelViewerHost.ViewDirection>(tag, out var direction))
            return;
        ModelViewerHost.SetOrthographicView(direction);
    }

    private void ModelResetView_Click(object sender, RoutedEventArgs e) => ModelViewerHost.ResetView();

    /// <summary>
    /// Swaps to the text panel showing the currently-selected animation's decoded structure (bone tracks
    /// and keyframes) — the raw view we used to open by default when a user clicked an .ani. Storing the
    /// owning model node lets <see cref="BackToModel_Click"/> restore the exact model-plus-animation
    /// context the user came from.
    /// </summary>
    private void ViewAniSource_Click(object sender, RoutedEventArgs e)
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
            TextViewer.Visibility = Visibility.Visible;
            AniSourceHeader.Visibility = Visibility.Visible;
            AniSourceHeaderLabel.Text = $"ANI: {_currentAnimationNode.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not decode animation source:\n{ex.Message}",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackToModel_Click(object sender, RoutedEventArgs e)
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

    private void ModelCompareToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || !_selectedNode.HasMod)
            return;
        VirtualFileSystem.OpenVariant variant = ModelCompareToggle.IsChecked == true
            ? VirtualFileSystem.OpenVariant.Mod
            : VirtualFileSystem.OpenVariant.Original;
        LoadSelectedResource(_selectedNode, variant);
    }

    private void ModelWireframe_Changed(object sender, RoutedEventArgs e)
    {
        _settings.ModelViewerWireframe = ModelWireframeCheck.IsChecked == true;
        _settings.Save();
        ModelViewerHost.ShowWireframe = _settings.ModelViewerWireframe;
    }

    private void ModelBackground_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModelBackgroundCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
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

    private void ModelSavePng_Click(object sender, RoutedEventArgs e)
    {
        byte[]? pixels = ModelViewerHost.RenderCurrentFrameToBytes(out int w, out int h);
        if (pixels is null)
        {
            MessageBox.Show(this, "Load a model first.", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string defaultStem = _currentModelNode is not null
            ? Path.GetFileNameWithoutExtension(_currentModelNode.Name)
            : "screenshot";

        var dialog = new SaveFileDialog
        {
            Title = "Save PNG",
            Filter = "PNG image (*.png)|*.png",
            FileName = SanitizeFileName(defaultStem) + ".png",
        };
        ApplyRememberedExportFolder(dialog);
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var outFile = File.Create(dialog.FileName);
            encoder.Save(outFile);
            RememberExportFolder(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Save failed:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ModelPlayPauseButton_Click(object sender, RoutedEventArgs e)
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

    private void ModelLoopCheck_Changed(object sender, RoutedEventArgs e) =>
        ModelViewerHost.Loop = ModelLoopCheck.IsChecked == true;

    private void ModelScrubSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _modelSliderDragging = true;
    }

    private void ModelScrubSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_modelSliderDragging)
            return;

        ModelViewerHost.SeekToFraction((float)e.NewValue);
    }

    private void ModelScrubSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _modelSliderDragging = false;
    }

    private void ModelPlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (ModelPanel.Visibility != Visibility.Visible)
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

    private void ImageCompareEnter_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is { HasMod: true } node)
            ShowCompareView(node);
    }

    private void ShowImages(IReadOnlyList<(string Name, DecodedImage Image)> images)
    {
        _currentImages = images.ToList();

        // Mirror the sound/model panels: show the A/B "Compare Mod" affordance whenever the current file
        // has a modded override. Users shouldn't have to right-click the tree to discover the compare
        // view exists.
        ImageCompareEnterButton.Visibility = _selectedNode is { HasMod: true } ? Visibility.Visible : Visibility.Collapsed;

        if (_currentImages.Count > 1)
        {
            ImageSelector.Visibility = Visibility.Visible;
            ImageSelector.ItemsSource = _currentImages.Select((entry, index) =>
                string.IsNullOrEmpty(entry.Name) ? $"[{index}]" : entry.Name).ToList();
            ImageSelector.SelectedIndex = 0;
        }
        else
        {
            ImageSelector.Visibility = Visibility.Collapsed;
            if (_currentImages.Count == 1)
                SetPreviewImage(_currentImages[0].Image);
        }

        ImagePanel.Visibility = Visibility.Visible;
    }

    private void ImageSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
        PreviewImage.Source = ToBitmapSource(image);
        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
        ApplyDefaultZoom(image);
    }

    private static BitmapSource? ToBitmapSource(DecodedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
            return null;

        int stride = image.Width * 4;
        var bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------------------------------------------------------------
    // Image zoom & panning
    // ---------------------------------------------------------------------

    private void ApplyDefaultZoom(DecodedImage image)
    {
        int maxDim = Math.Max(image.Width, image.Height);

        // Never shrink below native size by default; for small sprites, scale up to a comfortable
        // minimum size, rounded down to a whole multiple so nearest-neighbor scaling stays crisp.
        double zoom = maxDim <= 0 ? 1.0 : Math.Max(1.0, Math.Floor(PreferredDisplaySize / maxDim));
        SetZoom(Math.Clamp(zoom, 1.0, 8.0));
    }

    private void SetZoom(double zoom)
    {
        _imageZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ImageScale.ScaleX = _imageZoom;
        ImageScale.ScaleY = _imageZoom;
        ZoomLabel.Text = $"{_imageZoom * 100:0}%";
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_imageZoom * 1.25);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_imageZoom / 1.25);

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDisplayedImage is { } image)
            ApplyDefaultZoom(image);
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Source is null)
            return;

        e.Handled = true;
        SetZoom(e.Delta > 0 ? _imageZoom * 1.25 : _imageZoom / 1.25);
    }

    private void ImageScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is null)
            return;

        _panStart = e.GetPosition(ImageScrollViewer);
        _panStartHOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVOffset = ImageScrollViewer.VerticalOffset;
        ImageScrollViewer.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
    }

    private void ImageScrollViewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point pos = e.GetPosition(ImageScrollViewer);
        ImageScrollViewer.ScrollToHorizontalOffset(_panStartHOffset - (pos.X - start.X));
        ImageScrollViewer.ScrollToVerticalOffset(_panStartVOffset - (pos.Y - start.Y));
    }

    private void ImageScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panStart = null;
        ImageScrollViewer.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
    }

    // ---------------------------------------------------------------------
    // Sound playback (ResourceLoader always hands us a WAV file for WPF's MediaPlayer)
    // ---------------------------------------------------------------------

    private void SoundCompareToggle_Changed(object sender, RoutedEventArgs e)
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
            SubtitleText.Visibility = Visibility.Visible;
        }
        else
        {
            SubtitleText.Visibility = Visibility.Collapsed;
        }

        SoundSlider.Value = 0;
        // Neutral 0:00 labels until MediaOpened fires with the real duration.
        SoundCurrentTimeText.Text = "0:00";
        SoundTotalTimeText.Text = "0:00";
        SetPlayPauseIcon(playing: false);
        // Show the A/B toggle whenever the current selection has a mod override; keep its state in sync
        // with whichever variant is currently loaded.
        SoundCompareToggle.Visibility = _selectedNode is { HasMod: true } ? Visibility.Visible : Visibility.Collapsed;
        _mediaPlayer.Open(new Uri(sound.TempFilePath));

        // Render the waveform strip. The audio decode happens off-thread so a big WAV doesn't stall the
        // click; the actual DrawingImage is built on the UI thread because Freezables bind to whichever
        // dispatcher they're created on and touching them off-thread throws. Peaks are cached against
        // the sound instance so window resizes re-render cheaply without re-decoding the file.
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

        SoundPanel.Visibility = Visibility.Visible;

        if (_settings.AutoPlaySound)
            PlaySound();
    }

    /// <summary>
    /// Renders the cached peak buffer at a bar count sized to the panel's current width. Called on
    /// initial peak sample and on every WaveformImage SizeChanged so bar density stays uniform (≈ one
    /// bar per <see cref="WaveformPixelsPerBar"/> pixels) regardless of window size.
    /// </summary>
    private void RenderWaveformAtCurrentWidth()
    {
        if (_cachedWaveformPeaks is null || _cachedWaveformPeaks.Length == 0)
            return;

        double width = WaveformImage.ActualWidth;
        if (width < 1)
            width = SoundPanel.ActualWidth;
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
    private static readonly System.Windows.Media.Color WaveformColor = System.Windows.Media.Color.FromRgb(0xE8, 0xEC, 0xF4);

    private void WaveformImage_SizeChanged(object sender, SizeChangedEventArgs e)
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
        PlayPauseIcon.Source = (System.Windows.Media.ImageSource)FindResource(playing ? "PauseIcon" : "PlayIcon");

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
        _mediaPlayer.Stop();
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
            // final frame. Also snap the seek slider back and flip the transport icon to Play — the
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
        if (_mediaPlayer.NaturalDuration.HasTimeSpan)
        {
            SoundSlider.Maximum = Math.Max(0.01, _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds);
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
        TimeSpan total = _mediaPlayer.NaturalDuration.HasTimeSpan
            ? _mediaPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        SoundCurrentTimeText.Text = FormatSoundTime(pos);
        SoundTotalTimeText.Text = FormatSoundTime(total);
    }

    private static string FormatSoundTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_positionTimer.IsEnabled)
            PauseSound();
        else
            PlaySound();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Stop();
        _positionTimer.Stop();
        SoundSlider.Value = 0;
        SetPlayPauseIcon(playing: false);
        UpdateSoundTimeReadout();
    }

    private void SoundSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) =>
        _sliderDragging = true;

    private void SoundSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _sliderDragging = false;
        _mediaPlayer.Position = TimeSpan.FromSeconds(SoundSlider.Value);
        UpdateSoundTimeReadout();
    }

    // ---------------------------------------------------------------------
    // Video: ffmpeg.exe transcodes to MP4 on demand for WPF's MediaElement
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
        VideoPanel.Visibility = Visibility.Visible;
        VideoLabel.Text = $"{video.Kind} video -- transcoding for playback...";
        VideoPlayPauseButton.IsEnabled = false;
        VideoPlayer.Source = null;

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
        VideoPlayer.Source = new Uri(result.OutputPath!);
        if (_settings.AutoPlayVideo)
        {
            VideoPlayer.Play();
            _videoIsPlaying = true;
            VideoPlayPauseButton.Content = "Pause";
        }
        else
        {
            VideoPlayer.Pause();
            _videoIsPlaying = false;
            VideoPlayPauseButton.Content = "Play";
        }
        VideoPlayPauseButton.IsEnabled = true;
    }

    private void RemoveBackground_Changed(object sender, RoutedEventArgs e)
    {
        _settings.RemoveVideoBackground = RemoveBackgroundCheck.IsChecked == true;
        _settings.Save();
        if (_currentVideo is not null)
            _ = TranscodeAndPlayAsync(_currentVideo);
    }

    private void VideoZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged fires during XAML init before VideoScale/VideoZoomLabel are constructed.
        if (VideoScale is null || VideoZoomLabel is null)
            return;

        double zoom = VideoZoomSlider.Value / 100.0;
        VideoScale.ScaleX = zoom;
        VideoScale.ScaleY = zoom;
        VideoZoomLabel.Text = $"{VideoZoomSlider.Value:0}%";
    }

    private void VideoScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
        double newValue = Math.Clamp(VideoZoomSlider.Value * factor, VideoZoomSlider.Minimum, VideoZoomSlider.Maximum);
        VideoZoomSlider.Value = newValue;
        e.Handled = true;
    }

    private void StopVideo()
    {
        _videoLoadGeneration++;
        _videoIsPlaying = false;
        _currentVideo = null;
        VideoPlayer.Stop();
        VideoPlayer.Close();
        VideoPlayer.Source = null;
    }

    private void VideoPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source is null)
            return;

        if (_videoIsPlaying)
        {
            VideoPlayer.Pause();
            _videoIsPlaying = false;
            VideoPlayPauseButton.Content = "Play";
        }
        else
        {
            VideoPlayer.Play();
            _videoIsPlaying = true;
            VideoPlayPauseButton.Content = "Pause";
        }
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Rewind and pause; user can hit Play to loop.
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Pause();
        _videoIsPlaying = false;
        VideoPlayPauseButton.Content = "Play";
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        VideoLabel.Text = $"Playback failed: {e.ErrorException.Message}";
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContent is not ExternalVideoResource video)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(video.TempFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open external player:\n{ex.Message}", "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
