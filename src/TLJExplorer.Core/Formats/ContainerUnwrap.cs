namespace TLJExplorer.Core.Formats;

/// <summary>
/// Handles TLJ archive entries that are, byte-for-byte, an unmodified well-known
/// audio/video container stored under a TLJ-specific extension. No transcoding is
/// performed for these formats -- the archive simply renames the file. The known
/// mappings are:
/// <list type="bullet">
/// <item><description><c>.ovs</c> -&gt; raw Ogg Vorbis stream -&gt; export as <c>.ogg</c></description></item>
/// <item><description><c>.sss</c> -&gt; raw Smacker video stream -&gt; export as <c>.smk</c></description></item>
/// <item><description><c>.bbb</c> -&gt; raw Bink video stream -&gt; export as <c>.bik</c></description></item>
/// </list>
/// In every case, extraction is nothing more than a verbatim byte copy via
/// <see cref="Extract"/>; there is no header rewriting or reinterpretation involved.
/// </summary>
public static class ContainerUnwrap
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Copies the verbatim bytes of <paramref name="source"/> to a new file at
    /// <paramref name="destinationPath"/>. This is the entire "decode" step for
    /// <c>.ovs</c>/<c>.sss</c>/<c>.bbb</c> entries: they already contain valid Ogg Vorbis / Smacker /
    /// Bink bytes respectively.
    /// </summary>
    public static void ExtractToFile(Stream source, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
        source.CopyTo(destination, BufferSize);
    }

    /// <summary>
    /// Maps a TLJ container source extension to the extension of the well-known
    /// format it holds verbatim, for callers that want to name the extracted file
    /// appropriately (e.g. an export/handler layer). Case-insensitive; the leading
    /// dot is optional on input and always present on output.
    /// </summary>
    /// <param name="sourceExtension">One of <c>.ovs</c>, <c>.sss</c>, or <c>.bbb</c> (dot optional).</param>
    /// <returns>The corresponding extracted extension: <c>.ogg</c>, <c>.smk</c>, or <c>.bik</c>.</returns>
    /// <exception cref="ArgumentException">The extension is not one of the known passthrough containers.</exception>
    public static string GetExtractedExtension(string sourceExtension)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceExtension);

        var normalized = sourceExtension.StartsWith('.') ? sourceExtension : "." + sourceExtension;

        return normalized.ToLowerInvariant() switch
        {
            ".ovs" => ".ogg",
            ".sss" => ".smk",
            ".bbb" => ".bik",
            _ => throw new ArgumentException(
                $"'{sourceExtension}' is not a recognized passthrough container extension (expected .ovs, .sss, or .bbb).",
                nameof(sourceExtension)),
        };
    }
}
