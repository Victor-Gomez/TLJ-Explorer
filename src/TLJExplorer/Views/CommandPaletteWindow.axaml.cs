using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Views;

/// <summary>
/// Modal quick-open dialog: fuzzy-matches every file in the VFS against the query and returns the chosen
/// node via <see cref="Window.Close(object?)"/>. Enter opens the highlighted match, Esc cancels, up/down
/// move selection while focus stays in the query box (VS Code style).
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly List<PaletteEntry> _allEntries;

    public CommandPaletteWindow()
    {
        InitializeComponent();
        _allEntries = [];
    }

    public CommandPaletteWindow(VirtualFileSystem vfs) : this()
    {
        _allEntries = new List<PaletteEntry>();
        foreach (FsNode file in EnumerateFiles(vfs.Root))
        {
            _allEntries.Add(new PaletteEntry(file, file.DisplayName, file.GetPath()));
        }

        UpdateResults(string.Empty);
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void QueryBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        UpdateResults(QueryBox.Text ?? string.Empty);

    private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                Close(null);
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_DoubleTapped(object? sender, TappedEventArgs e) => Accept();

    private void Accept()
    {
        if (ResultsList.SelectedItem is not PaletteEntry entry)
            return;
        Close(entry.Node);
    }

    private void MoveSelection(int delta)
    {
        int count = ResultsList.ItemCount;
        if (count == 0)
            return;
        int next = ResultsList.SelectedIndex + delta;
        if (next < 0) next = 0;
        if (next >= count) next = count - 1;
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem!);
    }

    private void UpdateResults(string query)
    {
        IEnumerable<(PaletteEntry Entry, int Score)> scored = _allEntries
            .Select(e => (e, Score: FuzzyScore(e.Path, e.DisplayName, query)))
            .Where(x => x.Score > int.MinValue)
            .OrderByDescending(x => x.Score)
            .Take(200);

        ResultsList.ItemsSource = scored.Select(x => x.Entry).ToList();
        if (ResultsList.ItemCount > 0)
            ResultsList.SelectedIndex = 0;
    }

    /// <summary>
    /// Lightweight fuzzy score: every character of the query has to appear in order (subsequence match)
    /// somewhere in <paramref name="displayName"/> or <paramref name="path"/>. Consecutive matches, matches
    /// at the start of a segment, and case-preserving matches all bump the score. Returns
    /// <see cref="int.MinValue"/> when there's no subsequence match at all.
    /// </summary>
    private static int FuzzyScore(string path, string displayName, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 0;

        int nameScore = SubsequenceScore(displayName, query);
        int pathScore = SubsequenceScore(path, query);
        int best = Math.Max(nameScore, pathScore);
        if (best == int.MinValue)
            return int.MinValue;

        // Bias toward name matches so typing "s0042" prefers `s0042.isn` over some path that happens to
        // contain those letters spread across a dozen segments.
        if (nameScore != int.MinValue)
            best += 50;

        return best;
    }

    private static int SubsequenceScore(string haystack, string needle)
    {
        int h = 0, n = 0, score = 0, streak = 0;
        while (h < haystack.Length && n < needle.Length)
        {
            char hc = char.ToLowerInvariant(haystack[h]);
            char nc = char.ToLowerInvariant(needle[n]);
            if (hc == nc)
            {
                score += 1 + streak;
                if (h == 0 || haystack[h - 1] is '\\' or '/' or '_' or '-' or '.')
                    score += 5;
                if (haystack[h] == needle[n])
                    score += 1;
                streak++;
                n++;
            }
            else
            {
                streak = 0;
            }
            h++;
        }

        return n == needle.Length ? score : int.MinValue;
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

    private sealed record PaletteEntry(FsNode Node, string DisplayName, string Path);
}
