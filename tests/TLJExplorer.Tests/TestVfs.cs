using System.Reflection;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Tests;

/// <summary>
/// Builds a <see cref="VirtualFileSystem"/> with no install behind it.
/// <see cref="VirtualFileSystem.Init"/> insists on a real TLJ install (a root .xrc declaring
/// "LAIDBACK"), which tests can't supply and mostly don't need -- nodes built by hand already know
/// their own physical path.
/// </summary>
internal static class TestVfs
{
    public static VirtualFileSystem Empty(string baseDir, bool loadMods = true)
    {
        var vfs = (VirtualFileSystem)Activator.CreateInstance(
            typeof(VirtualFileSystem),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [new FsNode { NodeType = FsNodeType.Directory, Name = "x" }, baseDir, null],
            culture: null)!;

        vfs.LoadMods = loadMods;
        return vfs;
    }
}
