using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace TLJExplorer.Services;

/// <summary>
/// Thin wrapper around a LibVLCSharp <see cref="MediaPlayer"/> shaped to match the WPF
/// <c>System.Windows.Media.MediaPlayer</c>/<c>MediaElement</c> API the app used to call directly, so the
/// sound and video panel call sites need only a mechanical rename. Used headless (no attached
/// <c>VideoView</c>) for the sound panel; the video panel additionally attaches <see cref="NativePlayer"/>
/// to a <c>LibVLCSharp.Avalonia.VideoView</c>.
/// </summary>
/// <remarks>
/// LibVLC's native events fire on its own callback threads, not the UI thread -- unlike WPF's
/// MediaPlayer/MediaElement, whose events already arrive on the dispatcher. <see cref="MediaOpened"/> and
/// <see cref="MediaEnded"/> are re-posted onto <see cref="Dispatcher.UIThread"/> here so callers can touch
/// UI state directly, matching the WPF behavior callers were written against.
/// </remarks>
public sealed class LibVlcMediaPlayer : IDisposable
{
    public MediaPlayer NativePlayer { get; }

    /// <summary>Fires once the media's duration becomes known -- the closest LibVLC analog of WPF
    /// MediaPlayer's <c>MediaOpened</c>, which callers typically use to read <see cref="Duration"/>.</summary>
    public event EventHandler? MediaOpened;

    /// <summary>Fires when playback reaches the end of the media.</summary>
    public event EventHandler? MediaEnded;

    public LibVlcMediaPlayer()
    {
        NativePlayer = new MediaPlayer(LibVlcRuntime.Shared);
        NativePlayer.LengthChanged += (_, _) => Dispatcher.UIThread.Post(() => MediaOpened?.Invoke(this, EventArgs.Empty));
        NativePlayer.EndReached += (_, _) => Dispatcher.UIThread.Post(() => MediaEnded?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Attaches <paramref name="path"/> as the current media without starting playback (mirrors
    /// WPF MediaPlayer's <c>Open</c>).</summary>
    public void Open(string path)
    {
        using var media = new Media(LibVlcRuntime.Shared, path);
        NativePlayer.Media = media;
    }

    public void Play() => NativePlayer.Play();

    public void Pause() => NativePlayer.Pause();

    public void Stop() => NativePlayer.Stop();

    /// <summary>Detaches the current media (mirrors WPF MediaPlayer/MediaElement's <c>Close</c>).</summary>
    public void Close()
    {
        NativePlayer.Stop();
        NativePlayer.Media = null;
    }

    /// <summary>
    /// Fire-and-forget variant of <see cref="Close"/> that runs off the UI thread. <c>NativePlayer.Stop</c>
    /// is synchronous and, when a track is actively playing, can block for tens to hundreds of ms while
    /// LibVLC tears down its decoder/output -- long enough to visibly stall a tree click. Returns as soon
    /// as the background task is queued; LibVLC's own APIs are thread-safe, so subsequent calls (Open on a
    /// new track) can happen immediately without racing.
    /// </summary>
    public void CloseAsync() => Task.Run(Close);

    public bool HasDurationTimeSpan => NativePlayer.Length >= 0;

    /// <summary>Media duration, or <see langword="null"/> if not yet known (mirrors WPF's
    /// <c>NaturalDuration.HasTimeSpan</c> / <c>NaturalDuration.TimeSpan</c> pair).</summary>
    public TimeSpan? Duration => NativePlayer.Length >= 0 ? TimeSpan.FromMilliseconds(NativePlayer.Length) : null;

    public TimeSpan Position
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, NativePlayer.Time));
        set => NativePlayer.Time = (long)value.TotalMilliseconds;
    }

    public void Dispose()
    {
        NativePlayer.Dispose();
    }
}
