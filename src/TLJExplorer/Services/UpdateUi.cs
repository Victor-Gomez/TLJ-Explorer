using System.Threading.Tasks;
using Avalonia.Controls;
using TLJExplorer.Core.Settings;

namespace TLJExplorer.Services;

/// <summary>
/// The user-initiated half of the update flow -- "Check for Updates" clicked from the Help menu or the
/// About box. Always reports an outcome, including "you're up to date" and outright failures, because a
/// check the user asked for going silent reads as a broken button. The startup check is separate and
/// deliberately quiet (see <c>MainWindow.RunStartupUpdateCheckAsync</c>).
/// </summary>
public static class UpdateUi
{
    /// <summary>
    /// Runs a check against <see cref="AppSettings.ReleaseFeedUrl"/> and shows the result, offering to open
    /// the release page in a browser when something newer exists. An explicit check clears any previously
    /// skipped version: asking again means the user wants to hear about it.
    /// </summary>
    public static async Task CheckInteractiveAsync(Window owner, AppSettings settings)
    {
        UpdateCheckResult result = await UpdateChecker.CheckAsync(settings.ReleaseFeedUrl, AppInfo.Version);

        if (result.Error is not null)
        {
            await Dialogs.ShowMessageBox(
                owner,
                $"Could not check for updates.\n\n{result.Error}",
                "Check for Updates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        settings.LastUpdateCheckUtc = DateTime.UtcNow;

        if (!result.UpdateAvailable)
        {
            settings.Save();
            await Dialogs.ShowMessageBox(
                owner,
                $"TLJ Explorer {AppInfo.DisplayVersion} is up to date.",
                "Check for Updates",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        settings.SkippedUpdateVersion = null;
        settings.Save();

        MessageBoxResult choice = await Dialogs.ShowMessageBox(
            owner,
            $"Version {result.LatestVersion} is available. You have {AppInfo.DisplayVersion}.\n\n" +
            "Open the release page to download it?",
            "Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (choice == MessageBoxResult.Yes)
            OpenReleasePage(result.ReleaseUrl, settings);
    }

    /// <summary>
    /// Opens <paramref name="releaseUrl"/>, falling back to the repository's releases index if the feed
    /// didn't carry a per-release link.
    /// </summary>
    public static void OpenReleasePage(string? releaseUrl, AppSettings settings) =>
        SystemBrowser.TryOpen(releaseUrl ?? RepositoryUrls.Releases(settings.ReleaseFeedUrl));
}

/// <summary>
/// Derives the human-facing GitHub URLs from the configured API feed URL, so a fork that repoints
/// <see cref="AppSettings.ReleaseFeedUrl"/> gets correct links everywhere without a second setting.
/// </summary>
public static class RepositoryUrls
{
    private const string Fallback = "https://github.com/Victor-Gomez/TLJ-Explorer";

    /// <summary>Turns <c>https://api.github.com/repos/OWNER/REPO/releases/latest</c> into <c>https://github.com/OWNER/REPO</c>.</summary>
    public static string Home(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? uri))
            return Fallback;

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        // Expected shape: repos/OWNER/REPO/...
        if (segments.Length < 3 || !string.Equals(segments[0], "repos", StringComparison.OrdinalIgnoreCase))
            return Fallback;

        return $"https://github.com/{segments[1]}/{segments[2]}";
    }

    public static string Issues(string feedUrl) => $"{Home(feedUrl)}/issues";

    public static string Releases(string feedUrl) => $"{Home(feedUrl)}/releases/latest";
}
