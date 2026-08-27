using System.Diagnostics;

namespace TLJExplorer.Services;

/// <summary>
/// Hands a URL to whatever the OS considers the default browser. <see cref="ProcessStartInfo.UseShellExecute"/>
/// does the right thing on Windows but is a no-op for URLs on Linux, so that platform goes through
/// <c>xdg-open</c> explicitly.
/// </summary>
public static class SystemBrowser
{
    /// <summary>
    /// Opens <paramref name="url"/> externally. Returns <see langword="false"/> if the platform refused --
    /// no browser installed, no desktop session -- rather than throwing at the call site.
    /// </summary>
    public static bool TryOpen(string url)
    {
        // Only ever hand the shell an http(s) URL: UseShellExecute on a path or a custom scheme would
        // happily launch a local executable instead.
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warn($"Refusing to open non-web URL: {url}");
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", uri.AbsoluteUri) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("open", uri.AbsoluteUri) { UseShellExecute = true });

            return true;
        }
        catch (Exception ex)
        {
            Log.Exception($"Failed to open URL {uri.AbsoluteUri}", ex);
            return false;
        }
    }
}
