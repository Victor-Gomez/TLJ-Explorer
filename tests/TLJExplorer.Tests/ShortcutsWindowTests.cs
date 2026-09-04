using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using TLJExplorer.Services;
using TLJExplorer.Views;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// The cheat sheet builds its rows in code from <see cref="KeyboardShortcuts"/>, so this checks the XAML
/// loads and that every documented shortcut actually reaches the window.
/// </summary>
public class ShortcutsWindowTests : UiTestBase
{
    [AvaloniaFact]
    public void EveryDocumentedShortcutIsRendered()
    {
        ShortcutsWindow window = Show(new ShortcutsWindow());

        List<string> shown = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .ToList();

        Assert.All(KeyboardShortcuts.All, s =>
        {
            Assert.Contains(s.Gesture, shown);
            Assert.Contains(s.Description, shown);
        });
    }

    [AvaloniaFact]
    public void EveryGroupHeadingIsRendered()
    {
        ShortcutsWindow window = Show(new ShortcutsWindow());

        List<string> shown = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .ToList();

        Assert.All(KeyboardShortcuts.Groups, g => Assert.Contains(g, shown));
    }
}
