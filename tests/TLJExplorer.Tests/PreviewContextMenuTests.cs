using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Verifies the preview menu's wiring, not just its rules: that <c>PreviewContextMenu_Opening</c> maps a
/// <see cref="PreviewContextActions"/> onto the real MenuItems and their group separators.
/// </summary>
public class PreviewContextMenuTests : UiTestBase
{
    private MainWindow OpenMenuFor(ResourceContent? content, FsNode? node)
    {
        MainWindow window = Show(new MainWindow());

        typeof(MainWindow).GetField("_currentContent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, content);
        typeof(MainWindow).GetField("_selectedNode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, node);

        typeof(MainWindow).GetMethod("PreviewContextMenu_Opening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new ContextMenu(), new CancelEventArgs()]);

        return window;
    }

    private static bool Shown(MainWindow window, string name) =>
        window.FindControl<MenuItem>(name)!.IsVisible;

    private static FsNode ArchiveFile(string name = "april.xmg") => new()
    {
        NodeType = FsNodeType.File | FsNodeType.InArchive,
        Name = name,
        ArchivePath = "chapters.xarc",
    };

    [AvaloniaFact]
    public void OverAnImage_ShowsZoomAndExportsButNotModelEntries()
    {
        MainWindow window = OpenMenuFor(
            new ImageResource([("april", new DecodedImage(2, 2, new byte[16]))]), ArchiveFile());

        Assert.True(Shown(window, "PreviewMenuZoomIn"));
        Assert.True(Shown(window, "PreviewMenuFit"));
        Assert.True(Shown(window, "PreviewMenuExport"));
        Assert.True(Shown(window, "PreviewMenuExportRaw"));
        Assert.False(Shown(window, "PreviewMenuResetView"));
        Assert.False(Shown(window, "PreviewMenuWireframe"));
        Assert.False(Shown(window, "PreviewMenuCopyText"));
        Assert.False(Shown(window, "PreviewMenuOpenExternal"));
    }

    [AvaloniaFact]
    public void OverText_ShowsCopyEntriesInsteadOfZoom()
    {
        MainWindow window = OpenMenuFor(new TextResource("dump"), ArchiveFile("scene.xrc"));

        Assert.True(Shown(window, "PreviewMenuCopyText"));
        Assert.True(Shown(window, "PreviewMenuCopySelection"));
        Assert.False(Shown(window, "PreviewMenuZoomIn"));
    }

    [AvaloniaFact]
    public void OverAVideo_ShowsOpenExternally()
    {
        MainWindow window = OpenMenuFor(new ExternalVideoResource("clip.mp4", "Smacker"), ArchiveFile("clip.sss"));

        Assert.True(Shown(window, "PreviewMenuOpenExternal"));
        Assert.False(Shown(window, "PreviewMenuZoomIn"));
    }

    [AvaloniaFact]
    public void OverASound_HidesTheWholeViewGroupIncludingItsSeparator()
    {
        MainWindow window = OpenMenuFor(new SoundResource("clip.wav", null), ArchiveFile("line.isn"));

        Assert.False(window.FindControl<Separator>("PreviewMenuViewSeparator")!.IsVisible);
        Assert.True(Shown(window, "PreviewMenuExport"));
    }

    [AvaloniaFact]
    public void OverAnEmptyCanvas_CancelsInsteadOfShowingAnEmptyMenu()
    {
        MainWindow window = Show(new MainWindow());

        var args = new CancelEventArgs();
        typeof(MainWindow).GetMethod("PreviewContextMenu_Opening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new ContextMenu(), args]);

        Assert.True(args.Cancel);
    }
}
