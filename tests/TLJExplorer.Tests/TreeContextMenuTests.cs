using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.ViewModels;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Verifies the menu wiring, not just the rules: that <c>TreeContextMenu_Opening</c> maps a
/// <see cref="Services.TreeContextActions"/> onto the actual MenuItems, including the group separators.
/// </summary>
public class TreeContextMenuTests : UiTestBase
{
    private MainWindow OpenMenuFor(FsNode node)
    {
        MainWindow window = Show(new MainWindow());

        // With no PlacementTarget the handler falls back to the tree selection, which is the path this
        // test can drive without synthesising a real right-click over a realised TreeViewItem.
        var tree = window.FindControl<TreeView>("Tree")!;
        tree.ItemsSource = new[] { new FsNodeViewModel(node, TestVfs.Empty("unused")) };
        tree.SelectedItem = (tree.ItemsSource as FsNodeViewModel[])![0];

        typeof(MainWindow)
            .GetMethod("TreeContextMenu_Opening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new ContextMenu(), new CancelEventArgs()]);

        return window;
    }

    private static bool Shown(MainWindow window, string name) =>
        window.FindControl<MenuItem>(name)!.IsVisible;

    private static bool SeparatorShown(MainWindow window, string name) =>
        window.FindControl<Separator>(name)!.IsVisible;

    [AvaloniaFact]
    public void RightClickingAFolder_ShowsBatchExportAndHidesFileOnlyEntries()
    {
        MainWindow window = OpenMenuFor(new FsNode { NodeType = FsNodeType.Directory, Name = "AprilsRoom" });

        Assert.True(Shown(window, "TreeMenuBatchExport"));
        Assert.True(Shown(window, "TreeMenuCopyPath"));
        Assert.False(Shown(window, "TreeMenuExportItem"));
        Assert.False(Shown(window, "TreeMenuExportRaw"));
        Assert.False(Shown(window, "TreeMenuExtractAsMod"));
        // Whole mod group is hidden, so its separator must be too.
        Assert.False(SeparatorShown(window, "TreeMenuModSeparator"));
    }

    [AvaloniaFact]
    public void RightClickingAnArchivedFile_ShowsExportsAndHidesBatchExport()
    {
        MainWindow window = OpenMenuFor(new FsNode
        {
            NodeType = FsNodeType.File | FsNodeType.InArchive,
            Name = "april.xmg",
            ArchivePath = "chapters.xarc",
        });

        Assert.True(Shown(window, "TreeMenuExportItem"));
        Assert.True(Shown(window, "TreeMenuExportRaw"));
        Assert.True(Shown(window, "TreeMenuExtractAsMod"));
        Assert.False(Shown(window, "TreeMenuBatchExport"));
        Assert.False(Shown(window, "TreeMenuCompareMod"));
        Assert.False(Shown(window, "TreeMenuReveal"));
    }

    [AvaloniaFact]
    public void RightClickingAModdedFile_ShowsTheComparisonEntries()
    {
        MainWindow window = OpenMenuFor(new FsNode
        {
            NodeType = FsNodeType.File | FsNodeType.InArchive,
            Name = "april.xmg",
            ArchivePath = "chapters.xarc",
            ModPath = "mods/april.png",
        });

        Assert.True(Shown(window, "TreeMenuCompareMod"));
        Assert.True(Shown(window, "TreeMenuViewOriginal"));
        Assert.True(SeparatorShown(window, "TreeMenuModSeparator"));
        Assert.False(Shown(window, "TreeMenuExtractAsMod"));
    }

    [AvaloniaFact]
    public void OpeningOverNothing_CancelsInsteadOfShowingAnEmptyMenu()
    {
        MainWindow window = Show(new MainWindow());

        var args = new CancelEventArgs();
        typeof(MainWindow)
            .GetMethod("TreeContextMenu_Opening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new ContextMenu(), args]);

        Assert.True(args.Cancel);
    }
}
