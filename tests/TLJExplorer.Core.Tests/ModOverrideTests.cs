using TLJExplorer.Core.FileSystem;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class ModOverrideTests
{
    [Fact]
    public void OpenFile_LoadModsOn_ServesModStream_WhenNodeHasModPath()
    {
        // Synthetic node pointing at a temp mod file plus a phantom archive; with LoadMods on and
        // OpenVariant.Preferred we should receive the mod bytes.
        string modPath = Path.Combine(Path.GetTempPath(), $"mod-{Guid.NewGuid():N}.bin");
        byte[] modContent = System.Text.Encoding.ASCII.GetBytes("MOD_BYTES");
        File.WriteAllBytes(modPath, modContent);

        try
        {
            var vfs = MakeVfsWithRoot(out FsNode root);
            var file = new FsNode
            {
                NodeType = FsNodeType.File | FsNodeType.InArchive,
                Name = "sample.tm",
                ArchivePath = "N/A", // never opened because the mod wins
                ModPath = modPath,
                Size = modContent.Length,
                Offset = 0,
                Parent = root,
            };
            root.Children.Add(file);

            vfs.LoadMods = true;

            using Stream s = vfs.OpenFile(file);
            byte[] read = ReadAll(s);
            Assert.Equal(modContent, read);
        }
        finally
        {
            File.Delete(modPath);
        }
    }

    [Fact]
    public void OpenFile_LoadModsOff_ButExplicitModVariant_StillServesMod()
    {
        string modPath = Path.Combine(Path.GetTempPath(), $"mod-{Guid.NewGuid():N}.bin");
        byte[] modContent = [1, 2, 3, 4];
        File.WriteAllBytes(modPath, modContent);

        try
        {
            var vfs = MakeVfsWithRoot(out FsNode root);
            var file = new FsNode
            {
                NodeType = FsNodeType.File | FsNodeType.InArchive,
                Name = "sample.tm",
                ArchivePath = "N/A",
                ModPath = modPath,
                Size = modContent.Length,
                Parent = root,
            };
            root.Children.Add(file);

            vfs.LoadMods = false;

            using Stream s = vfs.OpenFile(file, VirtualFileSystem.OpenVariant.Mod);
            byte[] read = ReadAll(s);
            Assert.Equal(modContent, read);
        }
        finally
        {
            File.Delete(modPath);
        }
    }

    // Reflection back-door: VirtualFileSystem's constructor is private and Init requires a real TLJ
    // install. Since the modding logic only touches OpenFile + FsNode.ModPath, a minimal fake VFS is
    // sufficient for these unit tests.
    private static VirtualFileSystem MakeVfsWithRoot(out FsNode root)
    {
        root = new FsNode { NodeType = FsNodeType.Root | FsNodeType.Directory, Name = "x" };
        var ctor = typeof(VirtualFileSystem).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null,
            [typeof(FsNode), typeof(string), typeof(string)],
            modifiers: null);
        Assert.NotNull(ctor);
        return (VirtualFileSystem)ctor!.Invoke([root, "test", null!]);
    }

    private static byte[] ReadAll(Stream s)
    {
        using var mem = new MemoryStream();
        s.CopyTo(mem);
        return mem.ToArray();
    }
}
