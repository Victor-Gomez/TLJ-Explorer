using Avalonia.Media.Imaging;
using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Services;

/// <summary>
/// Discriminated result of loading a <see cref="TLJExplorer.Core.FileSystem.FsNode"/>'s bytes through
/// <see cref="ResourceLoader"/>. The UI switches on the concrete type to decide how to render it.
/// </summary>
public abstract record ResourceContent;

/// <summary>One or more named images decoded from a resource (a single XMG, or all sub-images of a TM texture).</summary>
public sealed record ImageResource(IReadOnlyList<(string Name, DecodedImage Image)> Images) : ResourceContent;

/// <summary>Plain monospace text: a structural dump (BIFF/XRC/CIR/ANI) or a raw hex dump.</summary>
public sealed record TextResource(string Text) : ResourceContent;

/// <summary>
/// A playable sound, already decoded/extracted to a temp file on disk (WAV for ISN family, OGG for OVS).
/// <see cref="Subtitle"/> holds the dialogue/subtitle lines from the owning node's ExtendedInfo, if any.
/// </summary>
public sealed record SoundResource(string TempFilePath, string[]? Subtitle) : ResourceContent;

/// <summary>
/// A video container (Smacker/Bink) extracted verbatim to a temp file. No in-app codec is available, so
/// the UI offers to open it in the user's default external player.
/// </summary>
public sealed record ExternalVideoResource(string TempFilePath, string Kind) : ResourceContent;

/// <summary>Loading the resource failed; <see cref="Message"/> is shown to the user in the text panel.</summary>
public sealed record ErrorResource(string Message) : ResourceContent;

/// <summary>
/// Side-by-side / wipe comparison of the original archive bytes and a mod override, decoded to the same
/// image kind. The compare panel renders <see cref="Original"/> as the base and clips <see cref="Modded"/>
/// on top by a user-draggable vertical wipe.
/// </summary>
public sealed record ImageCompareResource(
    string OriginalLabel,
    DecodedImage Original,
    string ModdedLabel,
    DecodedImage Modded) : ResourceContent;

/// <summary>
/// A static 3D model (CIR) ready to render. <see cref="SourceNode"/> is kept alongside so the UI can
/// kick off a potentially-slow texture resolution pass against the VFS without the loader itself
/// having to block on it.
/// </summary>
public sealed record ModelResource(CirModel Model, FsNode SourceNode) : ResourceContent;

/// <summary>
/// A composed static+animated view of an XRC scene folder ("April's room" etc.). <see cref="Base"/> is
/// the backdrop with any XMG/TM sprites blitted into it; <see cref="Overlays"/> are the video-driven
/// animated layers (Bink/Smacker), each with pre-decoded PNG frames and a target framerate, positioned
/// on top of the base.
/// </summary>
public sealed record SceneResource(Bitmap Base, IReadOnlyList<SceneAnimatedOverlay> Overlays) : ResourceContent;

/// <summary>
/// One animated overlay in a <see cref="SceneResource"/>: a series of pre-decoded frame bitmaps drawn
/// on top of the scene's base at <c>(X, Y)</c>, cycled at <see cref="Fps"/>.
/// </summary>
public sealed record SceneAnimatedOverlay(
    string Name,
    int X,
    int Y,
    IReadOnlyList<Bitmap> Frames,
    double Fps);
