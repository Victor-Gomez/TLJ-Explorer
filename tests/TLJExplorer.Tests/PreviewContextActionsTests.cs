using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Visibility rules for the preview canvas's context menu. The canvas hosts several very different
/// viewers, so the menu has to key off the loaded resource -- "Reset View" over an image or "Zoom In"
/// over a waveform is noise.
/// </summary>
public class PreviewContextActionsTests
{
    private static FsNode ArchiveFile(string name = "april.xmg") => new()
    {
        NodeType = FsNodeType.File | FsNodeType.InArchive,
        Name = name,
        ArchivePath = "chapters.xarc",
    };

    private static ImageResource AnImage() =>
        new([("april", new DecodedImage(2, 2, new byte[2 * 2 * 4]))]);

    [Fact]
    public void NothingLoaded_YieldsNothingToShow()
    {
        Assert.True(PreviewContextActions.For(null, null).IsEmpty);
        Assert.True(PreviewContextActions.For(null, ArchiveFile()).IsEmpty);
    }

    [Fact]
    public void Image_OffersZoomAndExportsButNoModelOrTextEntries()
    {
        PreviewContextActions actions = PreviewContextActions.For(AnImage(), ArchiveFile());

        Assert.True(actions.Zoom);
        Assert.True(actions.Export);
        Assert.True(actions.ExportRaw);
        Assert.False(actions.ResetView);
        Assert.False(actions.ToggleWireframe);
        Assert.False(actions.CopyText);
        Assert.False(actions.OpenExternally);
    }

    [Fact]
    public void Text_OffersCopyButNotZoom()
    {
        PreviewContextActions actions = PreviewContextActions.For(new TextResource("dump"), ArchiveFile("scene.xrc"));

        Assert.True(actions.CopyText);
        Assert.False(actions.Zoom);
        Assert.False(actions.ResetView);
    }

    [Fact]
    public void Video_OffersExternalPlayback()
    {
        PreviewContextActions actions = PreviewContextActions.For(
            new ExternalVideoResource("clip.mp4", "Smacker"), ArchiveFile("clip.sss"));

        Assert.True(actions.OpenExternally);
        Assert.False(actions.Zoom);
    }

    [Fact]
    public void Sound_OffersExportsOnly()
    {
        PreviewContextActions actions = PreviewContextActions.For(
            new SoundResource("clip.wav", null), ArchiveFile("line.isn"));

        Assert.True(actions.Export);
        Assert.True(actions.ExportRaw);
        Assert.False(actions.HasViewGroup);
    }

    // [AvaloniaFact], not [Fact]: constructing a WriteableBitmap needs the Skia platform
    // render interface, which only exists on the headless Avalonia app thread.
    [AvaloniaFact]
    public void Scene_IsZoomableAndExportableButHasNoSingleFileRawBytes()
    {
        // A scene is composited from a whole folder, so "the underlying file" doesn't exist.
        var scene = new SceneResource(new WriteableBitmap(new Avalonia.PixelSize(1, 1), new Avalonia.Vector(96, 96)), []);
        PreviewContextActions actions = PreviewContextActions.For(scene, new FsNode
        {
            NodeType = FsNodeType.Directory,
            Name = "AprilsRoom",
        });

        Assert.True(actions.Zoom);
        Assert.True(actions.Export);
        Assert.False(actions.ExportRaw);
    }

    [Fact]
    public void Error_OffersNoExports()
    {
        PreviewContextActions actions = PreviewContextActions.For(new ErrorResource("boom"), ArchiveFile());

        Assert.False(actions.Export);
        Assert.False(actions.ExportRaw);
        Assert.False(actions.HasExportGroup);
        // Copy Path still applies -- useful precisely when reporting a decode failure.
        Assert.True(actions.CopyPath);
    }

    [Fact]
    public void ArchivedFile_CannotBeRevealedInTheFileManager()
    {
        Assert.False(PreviewContextActions.For(AnImage(), ArchiveFile()).RevealInExplorer);

        var loose = new FsNode { NodeType = FsNodeType.File, Name = "april.png", ArchivePath = "mods/april.png" };
        Assert.True(PreviewContextActions.For(AnImage(), loose).RevealInExplorer);
    }

    [Fact]
    public void ViewSeparator_TracksItsOwnGroup()
    {
        Assert.True(PreviewContextActions.For(AnImage(), ArchiveFile()).HasViewGroup);
        Assert.False(PreviewContextActions.For(new SoundResource("c.wav", null), ArchiveFile()).HasViewGroup);
    }
}
