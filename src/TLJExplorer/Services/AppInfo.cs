using System.IO;
using System.Reflection;
using TLJExplorer.Core.Settings;

namespace TLJExplorer.Services;

/// <summary>
/// Facts about the running build and its environment, gathered in one place so Help &gt; About and the
/// update check can't disagree with each other. Everything here is cheap and side-effect free except
/// <see cref="DescribeLibVlc"/>, which deliberately touches the native engine.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    /// <summary>Numeric build version, e.g. <c>1.1.0</c>. Stamped from the release tag by CI.</summary>
    public static Version Version => Self.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Short display version -- the three numeric components without the trailing assembly revision,
    /// which .NET always pads to <c>0</c> and which means nothing to a user.
    /// </summary>
    public static string DisplayVersion => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    /// <summary>
    /// Full informational version. On CI builds this is <c>1.1.0+&lt;commit sha&gt;</c>; on local builds
    /// it's whatever the csproj says (<c>1.1.0-dev</c>).
    /// </summary>
    public static string InformationalVersion =>
        Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? DisplayVersion;

    /// <summary>
    /// The commit this build came from, or <see langword="null"/> for a local build with no <c>+sha</c>
    /// suffix. Trimmed to the usual 7-character short form for display.
    /// </summary>
    public static string? CommitSha
    {
        get
        {
            int plus = InformationalVersion.IndexOf('+');
            if (plus < 0 || plus == InformationalVersion.Length - 1)
                return null;

            string sha = InformationalVersion[(plus + 1)..];
            return sha.Length > 7 ? sha[..7] : sha;
        }
    }

    /// <summary>Version of the Avalonia assembly actually loaded, for bug reports.</summary>
    public static string AvaloniaVersion =>
        typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// One-line status of the ffmpeg dependency: whether the configured path resolves to a real file.
    /// Video preview and the scene viewer's animated overlays both fail without it, and "the video
    /// doesn't play" is otherwise indistinguishable from a decoder bug.
    /// </summary>
    public static string DescribeFfmpeg(AppSettings settings)
    {
        string path = settings.FfmpegPath;
        if (string.IsNullOrWhiteSpace(path))
            return "Not configured (Options > Settings > External Tools)";

        return File.Exists(path) ? $"Found: {path}" : $"Missing: {path}";
    }

    /// <summary>
    /// One-line status of LibVLC. Initialising the native engine is the only reliable probe, so this
    /// forces the shared instance and reports whatever it throws -- on Linux a missing distro package
    /// surfaces here rather than as a silent dead play button.
    /// </summary>
    public static string DescribeLibVlc()
    {
        try
        {
            return $"Loaded: {LibVlcRuntime.Shared.Version}";
        }
        catch (Exception ex)
        {
            return $"Unavailable: {ex.Message}";
        }
    }
}
