using System.Globalization;
using System.Text;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Core.Formats;

/// <summary>One dialogue line: which sound file, which speaker cue, the transcription itself.</summary>
public sealed record SubtitleEntry(string SoundPath, string Speaker, string Text, string Scene);

/// <summary>
/// Walks a loaded <see cref="VirtualFileSystem"/> and collects every dialogue line the XRC records
/// attached to the tree (via <see cref="XrcStructureReader"/> → <see cref="FsNode.ExtendedInfo"/>).
/// Used by the subtitle export command to write a CSV manifest of every voiced line in the game.
/// </summary>
public static class SubtitleIndex
{
    /// <summary>
    /// Enumerates every subtitle line reachable through the VFS tree. Deduped by (sound path, subtitle
    /// text) since the current XRC reader occasionally emits the sound entry twice for a dialogue record.
    /// </summary>
    public static IReadOnlyList<SubtitleEntry> Collect(VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        var results = new List<SubtitleEntry>();
        var seen = new HashSet<(string, string)>();

        foreach (FsNode node in EnumerateFiles(vfs.Root))
        {
            if (node.ExtendedInfo is not { Length: > 0 } lines)
                continue;

            (string speaker, string text) = ExtractSpeakerAndText(lines);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            string path = node.GetPath();
            if (!seen.Add((path, text)))
                continue;

            string scene = ClosestSceneName(node);
            results.Add(new SubtitleEntry(path, speaker, text, scene));
        }

        return results;
    }

    /// <summary>
    /// Writes a CSV file with columns <c>path,scene,speaker,text</c>. Escapes quotes and newlines per
    /// RFC 4180 so the output opens cleanly in Excel / LibreOffice / pandas.
    /// </summary>
    public static void WriteCsv(IEnumerable<SubtitleEntry> entries, string path)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("path,scene,speaker,text");
        foreach (SubtitleEntry entry in entries)
        {
            writer.Write(CsvEscape(entry.SoundPath));
            writer.Write(',');
            writer.Write(CsvEscape(entry.Scene));
            writer.Write(',');
            writer.Write(CsvEscape(entry.Speaker));
            writer.Write(',');
            writer.WriteLine(CsvEscape(entry.Text));
        }
    }

    /// <summary>
    /// Writes one SRT file per scene under <paramref name="outputDir"/>. Each subtitle is stamped 3 seconds
    /// apart starting at 0 — enough to open the file in a subtitle viewer, though timings won't match audio
    /// (we have no per-line timing in the XRC data). Returns the number of SRT files written.
    /// </summary>
    public static int WriteSrtPerScene(IEnumerable<SubtitleEntry> entries, string outputDir)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        Directory.CreateDirectory(outputDir);

        var byScene = entries.GroupBy(e => string.IsNullOrEmpty(e.Scene) ? "_root" : e.Scene, StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;

        foreach (var group in byScene)
        {
            string safe = SanitizeFileName(group.Key);
            string path = Path.Combine(outputDir, safe + ".srt");
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            int index = 1;
            TimeSpan cursor = TimeSpan.Zero;
            foreach (SubtitleEntry entry in group)
            {
                TimeSpan start = cursor;
                TimeSpan end = cursor + TimeSpan.FromSeconds(3);
                writer.WriteLine(index.ToString(CultureInfo.InvariantCulture));
                writer.Write(FormatSrtTime(start));
                writer.Write(" --> ");
                writer.WriteLine(FormatSrtTime(end));
                if (!string.IsNullOrEmpty(entry.Speaker))
                    writer.WriteLine($"<i>{entry.Speaker}</i>");
                writer.WriteLine(entry.Text);
                writer.WriteLine();

                cursor = end + TimeSpan.FromMilliseconds(500);
                index++;
            }

            fileCount++;
        }

        return fileCount;
    }

    private static (string Speaker, string Text) ExtractSpeakerAndText(string[] lines)
    {
        // XrcStructureReader emits ["[soundName]", "", subtitle]. Treat the first bracketed line as the
        // speaker identifier (stripping the brackets) and everything else joined together as the text.
        string speaker = string.Empty;
        var textParts = new List<string>();
        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string trimmed = raw.Trim();
            if (speaker.Length == 0 && trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                speaker = trimmed[1..^1];
                continue;
            }

            textParts.Add(trimmed);
        }

        return (speaker, string.Join(' ', textParts));
    }

    /// <summary>
    /// Walks up from <paramref name="node"/> to the first directory whose name looks like an XRC scene
    /// folder (any directory that isn't the root). Used to group subtitles by scene for SRT export.
    /// </summary>
    private static string ClosestSceneName(FsNode node)
    {
        for (FsNode? cursor = node.Parent; cursor is not null; cursor = cursor.Parent)
        {
            if (cursor.Parent is null)
                break;
            if ((cursor.NodeType & FsNodeType.Directory) != 0)
                return cursor.Name;
        }
        return string.Empty;
    }

    private static IEnumerable<FsNode> EnumerateFiles(FsNode node)
    {
        foreach (FsNode child in node.Children)
        {
            if ((child.NodeType & FsNodeType.File) != 0)
                yield return child;
            if ((child.NodeType & FsNodeType.Directory) != 0)
            {
                foreach (FsNode desc in EnumerateFiles(child))
                    yield return desc;
            }
        }
    }

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        bool needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!needsQuote)
            return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string FormatSrtTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            buffer[i] = invalid.Contains(name[i]) ? '_' : name[i];
        string result = new(buffer);
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
