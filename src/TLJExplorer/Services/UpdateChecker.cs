using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TLJExplorer.Services;

/// <summary>Outcome of one update check. <see cref="Error"/> being non-null means the check never completed.</summary>
public readonly record struct UpdateCheckResult(
    bool UpdateAvailable,
    string? LatestVersion,
    string? ReleaseUrl,
    string? Error)
{
    public static UpdateCheckResult Failed(string error) => new(false, null, null, error);
    public static UpdateCheckResult UpToDate(string latest) => new(false, latest, null, null);
    public static UpdateCheckResult Available(string latest, string? url) => new(true, latest, url, null);
}

/// <summary>
/// Asks a GitHub-shaped release feed whether a newer build exists. Deliberately read-only: it reports and
/// links, and never downloads or replaces anything -- releases ship as self-contained archives with no
/// installer, so self-updating would mean rewriting a running binary's own directory.
/// </summary>
public static class UpdateChecker
{
    // GitHub rejects requests with no User-Agent outright, so one is mandatory rather than cosmetic.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", $"TLJExplorer/{AppInfo.DisplayVersion}");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    /// <summary>How stale a <see cref="Core.Settings.AppSettings.LastUpdateCheckUtc"/> may be before a startup check runs again.</summary>
    public static readonly TimeSpan StartupCheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Parses a release tag (<c>v1.2.0</c>, <c>1.2</c>, <c>v1.2.0-beta.1</c>) into a comparable version,
    /// or <see langword="null"/> if it isn't version-shaped at all. Any pre-release suffix is dropped:
    /// <see cref="System.Version"/> can't represent one, and the feed we read excludes pre-releases anyway.
    /// </summary>
    public static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        int dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
            text = text[..dash];

        return Version.TryParse(text, out Version? parsed) ? Normalize(parsed) : null;
    }

    /// <summary>
    /// Whether <paramref name="tag"/> names a release newer than <paramref name="current"/>. An
    /// unparseable tag is treated as "not newer" -- a malformed feed must never nag the user.
    /// </summary>
    public static bool IsNewer(Version current, string? tag)
    {
        Version? candidate = TryParseTag(tag);
        return candidate is not null && candidate > Normalize(current);
    }

    /// <summary>
    /// Flattens a version to major.minor.build. Assembly versions always carry a fourth revision
    /// component (padded to 0) that release tags never do, so comparing unflattened values makes
    /// <c>1.2.0.0</c> and <c>1.2.0</c> look different.
    /// </summary>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);

    /// <summary>
    /// Reads <paramref name="feedUrl"/> and compares its <c>tag_name</c> against <paramref name="current"/>.
    /// Never throws: network, timeout, rate-limit and malformed-JSON failures all come back as
    /// <see cref="UpdateCheckResult.Error"/> for the caller to show or swallow.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        string feedUrl,
        Version current,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string json = await Http.GetStringAsync(feedUrl, cancellationToken).ConfigureAwait(false);
            return Evaluate(json, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            Log.Warn($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Interprets one release-feed document. Split out from <see cref="CheckAsync"/> so the comparison
    /// can be tested without a network round-trip.
    /// </summary>
    public static UpdateCheckResult Evaluate(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string? tag = root.TryGetProperty("tag_name", out JsonElement tagElement) && tagElement.ValueKind == JsonValueKind.String
            ? tagElement.GetString()
            : null;

        if (TryParseTag(tag) is null)
            return UpdateCheckResult.Failed("The release feed did not contain a recognisable version tag.");

        string? url = root.TryGetProperty("html_url", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;

        return IsNewer(current, tag)
            ? UpdateCheckResult.Available(tag!, url)
            : UpdateCheckResult.UpToDate(tag!);
    }
}
