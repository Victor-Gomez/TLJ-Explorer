using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Covers the comparison half of the update check -- the part that decides whether to nag the user.
/// Getting this wrong is either a silent "no updates ever" or a banner that won't go away, and neither
/// shows up in a build.
/// </summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("V1.2.0", "1.2.0")]
    [InlineData("  v1.2.0  ", "1.2.0")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v1.2.0-beta.1", "1.2.0")]
    [InlineData("v1.2.0+abc123", "1.2.0")]
    public void TryParseTag_NormalizesReleaseTags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateChecker.TryParseTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-candidate")]
    public void TryParseTag_ReturnsNullForNonVersionTags(string? tag)
    {
        Assert.Null(UpdateChecker.TryParseTag(tag));
    }

    [Theory]
    [InlineData("1.1.0", "v1.2.0", true)]
    [InlineData("1.1.0", "v1.1.1", true)]
    [InlineData("1.1.0", "v2.0.0", true)]
    [InlineData("1.1.0", "v1.1.0", false)]
    [InlineData("1.1.0", "v1.0.9", false)]
    [InlineData("1.1.0", "garbage", false)]
    [InlineData("1.1.0", null, false)]
    public void IsNewer_ComparesNumerically(string current, string? tag, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(Version.Parse(current), tag));
    }

    [Fact]
    public void IsNewer_IgnoresTheAssemblyRevisionComponent()
    {
        // Assembly versions always carry a fourth component; release tags never do. Without
        // normalization 1.1.0.0 vs 1.1.0 would compare as different and nag on every launch.
        Assert.False(UpdateChecker.IsNewer(new Version(1, 1, 0, 0), "v1.1.0"));
    }

    [Fact]
    public void Evaluate_ReportsAnAvailableUpdateWithItsReleaseUrl()
    {
        const string json = """
            { "tag_name": "v2.0.0", "html_url": "https://example.invalid/releases/v2.0.0" }
            """;

        UpdateCheckResult result = UpdateChecker.Evaluate(json, new Version(1, 1, 0));

        Assert.True(result.UpdateAvailable);
        Assert.Equal("v2.0.0", result.LatestVersion);
        Assert.Equal("https://example.invalid/releases/v2.0.0", result.ReleaseUrl);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_ReportsUpToDateWhenTheFeedMatchesTheRunningBuild()
    {
        UpdateCheckResult result = UpdateChecker.Evaluate("""{ "tag_name": "v1.1.0" }""", new Version(1, 1, 0));

        Assert.False(result.UpdateAvailable);
        Assert.Equal("v1.1.0", result.LatestVersion);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_TreatsAFeedWithoutAUsableTagAsAFailureRatherThanAnUpdate()
    {
        UpdateCheckResult result = UpdateChecker.Evaluate("""{ "message": "Not Found" }""", new Version(1, 1, 0));

        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Evaluate_ToleratesAnAvailableReleaseWithNoHtmlUrl()
    {
        UpdateCheckResult result = UpdateChecker.Evaluate("""{ "tag_name": "v9.9.9" }""", new Version(1, 1, 0));

        Assert.True(result.UpdateAvailable);
        Assert.Null(result.ReleaseUrl);
    }
}

/// <summary>
/// The About box and the update banner derive every github.com link from the configured API feed URL,
/// so a fork only has to repoint one setting.
/// </summary>
public class RepositoryUrlsTests
{
    [Fact]
    public void Home_DerivesTheRepositoryPageFromTheApiFeedUrl()
    {
        Assert.Equal(
            "https://github.com/Victor-Gomez/TLJ-Explorer",
            RepositoryUrls.Home("https://api.github.com/repos/Victor-Gomez/TLJ-Explorer/releases/latest"));
    }

    [Fact]
    public void Home_DerivesAForksUrlsToo()
    {
        Assert.Equal(
            "https://github.com/someone/their-fork",
            RepositoryUrls.Home("https://api.github.com/repos/someone/their-fork/releases/latest"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://example.invalid/some/other/feed.json")]
    [InlineData("https://api.github.com/repos/incomplete")]
    public void Home_FallsBackToTheCanonicalRepoForUnrecognisedFeeds(string feedUrl)
    {
        Assert.Equal("https://github.com/Victor-Gomez/TLJ-Explorer", RepositoryUrls.Home(feedUrl));
    }

    [Fact]
    public void IssuesAndReleases_HangOffTheDerivedHome()
    {
        const string feed = "https://api.github.com/repos/someone/their-fork/releases/latest";

        Assert.Equal("https://github.com/someone/their-fork/issues", RepositoryUrls.Issues(feed));
        Assert.Equal("https://github.com/someone/their-fork/releases/latest", RepositoryUrls.Releases(feed));
    }
}

/// <summary>Only http(s) URLs may reach the shell -- see <see cref="SystemBrowser"/>.</summary>
public class SystemBrowserTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void TryOpen_RefusesAnythingThatIsNotAWebUrl(string url)
    {
        Assert.False(SystemBrowser.TryOpen(url));
    }
}
