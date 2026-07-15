using LibVLCSharp.Shared;

namespace TLJExplorer.Services;

/// <summary>
/// Owns the single shared <see cref="LibVLC"/> instance used by every <see cref="LibVlcMediaPlayer"/> in
/// the app (the sound panel and the video panel each get their own player, but they share one underlying
/// libvlc engine instance -- creating more than one <see cref="LibVLC"/> per process is wasteful and
/// unnecessary). Replaces WPF's <c>System.Windows.Media.MediaPlayer</c>/<c>MediaElement</c>, which are
/// Windows Media Foundation-backed and don't exist on Linux.
/// </summary>
public static class LibVlcRuntime
{
    private static readonly Lazy<LibVLC> Instance = new(() =>
    {
        // Fully qualified: unqualified "Core" resolves to the sibling TLJExplorer.Core namespace
        // instead of LibVLCSharp.Shared.Core here, since both share the enclosing TLJExplorer namespace.
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC();
    });

    public static LibVLC Shared => Instance.Value;
}
