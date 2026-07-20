using System.Reflection;
using TLJExplorer.Core.FileSystem;
using Xunit;

namespace TLJExplorer.Core.Tests;

/// <summary>
/// Pins Stark's PNG-overrides-XMG mod rule: a loose <c>foo.png</c> in the archive's <c>xarc/</c>
/// sibling folder overrides the archived <c>foo.xmg</c> entry, even though the extensions differ.
/// (See ScummVM <c>engines/stark/resources/image.cpp</c> <c>loadPNGOverride</c>.)
/// </summary>
public class ModPngForXmgOverrideTests
{
    [Fact]
    public void ResolveModTarget_PngLooseFile_MatchesXmgArchiveEntry()
    {
        // Build a tiny FsNode "archive" containing one XMG entry, then invoke ResolveModTarget via
        // reflection with a loose file named the same stem but a .png extension.
        var archived = new FsNode
        {
            NodeType = FsNodeType.File | FsNodeType.InArchive,
            Name = "alchemist_essence_blue_inv.xmg",
            ArchivePath = "dummy.xarc",
            Size = 42,
        };

        var byName = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase)
        {
            [archived.Name] = archived,
        };

        MethodInfo? m = typeof(VirtualFileSystem).GetMethod(
            "ResolveModTarget",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);

        var target = (FsNode?)m!.Invoke(null, [byName, "alchemist_essence_blue_inv.png"]);
        Assert.Same(archived, target);
    }

    [Fact]
    public void ResolveModTarget_ExactNameStillMatches()
    {
        var archived = new FsNode { NodeType = FsNodeType.File, Name = "walk.ani" };
        var byName = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase) { [archived.Name] = archived };

        MethodInfo m = typeof(VirtualFileSystem).GetMethod("ResolveModTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        var target = (FsNode?)m.Invoke(null, [byName, "walk.ani"]);
        Assert.Same(archived, target);
    }

    [Theory]
    [InlineData("010f01.bbb", "010f01.bik")]
    [InlineData("intro.sss", "intro.smk")]
    [InlineData("music.ovs", "music.ogg")]
    public void ResolveModTarget_ContainerPassthroughSwap_MatchesArchiveEntry(string archiveName, string modName)
    {
        var archived = new FsNode { NodeType = FsNodeType.File | FsNodeType.InArchive, Name = archiveName };
        var byName = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase) { [archived.Name] = archived };

        MethodInfo m = typeof(VirtualFileSystem).GetMethod("ResolveModTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        var target = (FsNode?)m.Invoke(null, [byName, modName]);
        Assert.Same(archived, target);
    }

    [Fact]
    public void ResolveModTarget_UnrelatedExtensionSwap_ReturnsNull()
    {
        var archived = new FsNode { NodeType = FsNodeType.File, Name = "something.tm" };
        var byName = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase) { [archived.Name] = archived };

        MethodInfo m = typeof(VirtualFileSystem).GetMethod("ResolveModTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        // A .png doesn't currently override a .tm — only .xmg. Result should be null so the loose file
        // adds as a new entry instead of being (wrongly) attached to the TM as an override.
        var target = (FsNode?)m.Invoke(null, [byName, "something.png"]);
        Assert.Null(target);
    }
}
