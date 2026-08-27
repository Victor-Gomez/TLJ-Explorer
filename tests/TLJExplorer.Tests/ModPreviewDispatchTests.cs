using System.IO;
using Avalonia.Headless.XUnit;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Core.Settings;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Pins the dispatch half of the mod contract. <see cref="VirtualFileSystem"/> deliberately lets a mod
/// override an archive entry with a *different* extension (.png for .xmg, .ogg for .ovs, .bik/.smk for
/// .bbb/.sss), and <see cref="ResourceLoader"/> dispatches on the extension of whichever file is being
/// served -- so the mod-side extensions have to be routable too. When they aren't, a modded entry
/// silently falls through to the raw hex dump instead of previewing, which is what happened to every
/// PNG-modded .xmg.
/// </summary>
public class ModPreviewDispatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tlj-dispatch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static DecodedImage SolidImage(int w = 2, int h = 2)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0x20; // B
            pixels[i + 1] = 0x40; // G
            pixels[i + 2] = 0x80; // R
            pixels[i + 3] = 0xFF; // A
        }

        return new DecodedImage(w, h, pixels);
    }

    /// <summary>
    /// An archive entry node standing in for a real one, with <paramref name="modPath"/> attached as its
    /// override. Only the mod path is ever opened here, so no real .xarc is needed.
    /// </summary>
    private static FsNode ModdedEntry(string archiveEntryName, string modPath) => new()
    {
        NodeType = FsNodeType.File | FsNodeType.InArchive,
        Name = archiveEntryName,
        ArchivePath = "unused.xarc",
        ModPath = modPath,
        Size = 1,
    };

    private VirtualFileSystem EmptyVfsWithModsOn() => TestVfs.Empty(_dir);

    [AvaloniaFact]
    public void PngModOverridingAnXmgEntry_PreviewsAsAnImageRatherThanAHexDump()
    {
        string modPath = Path.Combine(_dir, "april_portrait.png");
        PngWriter.Write(SolidImage(), modPath);

        FsNode node = ModdedEntry("april_portrait.xmg", modPath);

        ResourceContent content = ResourceLoader.Load(
            node, EmptyVfsWithModsOn(), new AppSettings(), new TempFileTracker());

        var image = Assert.IsType<ImageResource>(content);
        Assert.Equal(2, Assert.Single(image.Images).Image.Width);
    }

    [AvaloniaFact]
    public void LooseStandalonePngFile_PreviewsAsAnImage()
    {
        // Not a mod at all: the VFS also surfaces loose files that match no archive entry as their own
        // nodes, and a .png dropped in a mods folder is the common case.
        string path = Path.Combine(_dir, "standalone.png");
        PngWriter.Write(SolidImage(4, 4), path);

        var node = new FsNode
        {
            NodeType = FsNodeType.File,
            Name = "standalone.png",
            ArchivePath = path,
            Size = new FileInfo(path).Length,
        };

        ResourceContent content = ResourceLoader.Load(
            node, EmptyVfsWithModsOn(), new AppSettings(), new TempFileTracker());

        var image = Assert.IsType<ImageResource>(content);
        Assert.Equal(4, Assert.Single(image.Images).Image.Width);
    }

    [AvaloniaFact]
    public void UnmoddedXmgEntry_StillRoutesToTheXmgDecoder()
    {
        // Guards the other direction: the .png route must not have disturbed plain archive entries.
        // Deliberately not valid XMG bytes -- reaching the decoder at all is the point, and a decode
        // failure surfaces as an ErrorResource, never as a hex dump.
        string path = Path.Combine(_dir, "raw.xmg");
        File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);

        var node = new FsNode
        {
            NodeType = FsNodeType.File,
            Name = "raw.xmg",
            ArchivePath = path,
            Size = 4,
        };

        ResourceContent content = ResourceLoader.Load(
            node, EmptyVfsWithModsOn(), new AppSettings(), new TempFileTracker());

        Assert.IsNotType<TextResource>(content);
    }
}
