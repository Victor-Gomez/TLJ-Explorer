using TLJExplorer.Core.FileSystem;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class FsNodeTests
{
    [Fact]
    public void DisplayName_NoFriendlyName_FallsBackToName()
    {
        var node = new FsNode { Name = "physical.xrc" };

        Assert.Equal("physical.xrc", node.DisplayName);
    }

    [Fact]
    public void DisplayName_WithFriendlyName_PrefersFriendlyName()
    {
        var node = new FsNode { Name = "physical.xrc", FriendlyName = "Nice Name" };

        Assert.Equal("Nice Name", node.DisplayName);
    }

    [Fact]
    public void HasMod_NullModPath_IsFalse()
    {
        var node = new FsNode { ModPath = null };
        Assert.False(node.HasMod);
    }

    [Fact]
    public void HasMod_EmptyModPath_IsFalse()
    {
        var node = new FsNode { ModPath = "" };
        Assert.False(node.HasMod);
    }

    [Fact]
    public void HasMod_SetModPath_IsTrue()
    {
        var node = new FsNode { ModPath = @"C:\mods\file.png" };
        Assert.True(node.HasMod);
    }

    [Fact]
    public void GetPath_RootNode_ReturnsBackslash()
    {
        var root = new FsNode { Name = "root", NodeType = FsNodeType.Root };

        Assert.Equal(@"\", root.GetPath());
    }

    [Fact]
    public void GetPath_NestedNode_JoinsAncestorNamesWithBackslash()
    {
        var root = new FsNode { Name = "root", NodeType = FsNodeType.Root };
        var dir = new FsNode { Name = "chapter1", Parent = root };
        var file = new FsNode { Name = "sprite.xmg", Parent = dir };
        root.Children.Add(dir);
        dir.Children.Add(file);

        Assert.Equal(@"\chapter1\sprite.xmg", file.GetPath());
    }

    [Fact]
    public void GetPath_NodeWithoutParent_ReturnsJustItsOwnBackslashPrefixedName()
    {
        var orphan = new FsNode { Name = "loose" };

        Assert.Equal(@"\", orphan.GetPath());
    }

    [Fact]
    public void NodeType_IsFlags_CanCombineFileAndInArchive()
    {
        var node = new FsNode { NodeType = FsNodeType.File | FsNodeType.InArchive };

        Assert.True(node.NodeType.HasFlag(FsNodeType.File));
        Assert.True(node.NodeType.HasFlag(FsNodeType.InArchive));
        Assert.False(node.NodeType.HasFlag(FsNodeType.Directory));
    }

    [Fact]
    public void Children_DefaultsToEmptyList()
    {
        var node = new FsNode();

        Assert.Empty(node.Children);
    }
}
