using TLJExplorer.Core.FileSystem;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// The tree context menu's visibility rules. Previously every entry showed for every node and you only
/// learned an action didn't apply by clicking it and reading the message box that came back.
/// </summary>
public class TreeContextActionsTests
{
    private static FsNode ArchiveFile(string name = "april.xmg", string? modPath = null) => new()
    {
        NodeType = FsNodeType.File | FsNodeType.InArchive,
        Name = name,
        ArchivePath = "install/chapters.xarc",
        ModPath = modPath,
    };

    private static FsNode LooseFile(string name = "april.png") => new()
    {
        NodeType = FsNodeType.File,
        Name = name,
        ArchivePath = $"install/xarc/{name}",
    };

    private static FsNode Folder(string name = "AprilsRoom") => new()
    {
        NodeType = FsNodeType.Directory,
        Name = name,
    };

    [Fact]
    public void NoNode_YieldsNothingToShow()
    {
        Assert.True(TreeContextActions.For(null).IsEmpty);
    }

    [Fact]
    public void Folder_OffersBatchExportButNotSingleFileActions()
    {
        TreeContextActions actions = TreeContextActions.For(Folder());

        Assert.True(actions.BatchExportFolder);
        Assert.True(actions.CopyPath);
        Assert.False(actions.ExportItem);
        Assert.False(actions.ExportRaw);
        Assert.False(actions.ExtractAsMod);
        Assert.False(actions.RevealInExplorer);
        Assert.False(actions.HasModGroup);
    }

    [Fact]
    public void ArchivedFile_OffersExportsAndModExtractionButNotBatchExport()
    {
        TreeContextActions actions = TreeContextActions.For(ArchiveFile());

        Assert.True(actions.ExportItem);
        Assert.True(actions.ExportRaw);
        Assert.True(actions.ExtractAsMod);
        Assert.False(actions.BatchExportFolder);
        // It lives inside a .xarc, so there's no file on disk for the OS file manager to reveal.
        Assert.False(actions.RevealInExplorer);
    }

    [Fact]
    public void LooseFile_CanBeRevealedButNotExtractedAsAMod()
    {
        TreeContextActions actions = TreeContextActions.For(LooseFile());

        Assert.True(actions.RevealInExplorer);
        // Already a loose file on disk -- editable in place, nothing to extract.
        Assert.False(actions.ExtractAsMod);
    }

    [Fact]
    public void UnmoddedFile_HidesTheMod_ComparisonEntries()
    {
        TreeContextActions actions = TreeContextActions.For(ArchiveFile());

        Assert.False(actions.ViewOriginal);
        Assert.False(actions.CompareMod);
    }

    [Fact]
    public void ModdedFile_OffersComparisonButNoLongerOffersExtraction()
    {
        TreeContextActions actions = TreeContextActions.For(ArchiveFile(modPath: "mods/april.png"));

        Assert.True(actions.ViewOriginal);
        Assert.True(actions.CompareMod);
        Assert.True(actions.HasModGroup);
        // It already has an override to edit.
        Assert.False(actions.ExtractAsMod);
    }

    [Fact]
    public void PlayLocalized_OnlyShowsWhenALocalisedCounterpartExists()
    {
        var folder = Folder();
        var english = new FsNode { NodeType = FsNodeType.File, Name = "line.isn", IsLocalized = false, Parent = folder };
        var lonely = new FsNode { NodeType = FsNodeType.File, Name = "other.isn", IsLocalized = false, Parent = folder };
        folder.Children.Add(english);
        folder.Children.Add(lonely);

        Assert.False(TreeContextActions.For(english).PlayLocalized);

        folder.Children.Add(new FsNode
        {
            NodeType = FsNodeType.File, Name = "line.isn", IsLocalized = true, Parent = folder,
        });

        Assert.True(TreeContextActions.For(english).PlayLocalized);
        Assert.False(TreeContextActions.For(lonely).PlayLocalized);
    }

    [Fact]
    public void FindLocalizedSibling_MatchesCaseInsensitivelyAndSkipsTheNodeItself()
    {
        var folder = Folder();
        var english = new FsNode { NodeType = FsNodeType.File, Name = "Line.isn", IsLocalized = false, Parent = folder };
        var localised = new FsNode { NodeType = FsNodeType.File, Name = "line.ISN", IsLocalized = true, Parent = folder };
        folder.Children.Add(english);
        folder.Children.Add(localised);

        Assert.Same(localised, TreeContextActions.FindLocalizedSibling(english));
        Assert.Same(english, TreeContextActions.FindLocalizedSibling(localised));
    }

    [Fact]
    public void SeparatorGroups_TrackTheirOwnEntries()
    {
        // A separator whose whole group is hidden would render as a stray line.
        Assert.False(TreeContextActions.For(Folder()).HasModGroup);
        Assert.True(TreeContextActions.For(Folder()).HasExportGroup);
        Assert.True(TreeContextActions.For(ArchiveFile()).HasExportGroup);
    }
}
