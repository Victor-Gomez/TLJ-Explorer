using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// The F1 cheat sheet is only worth having if it matches reality, so these pin its shape. They can't
/// prove a listed gesture is actually handled -- that lives in MainWindow's key handler -- but they do
/// catch an empty group, a duplicated entry, or a shortcut listed under the wrong heading.
/// </summary>
public class KeyboardShortcutsTests
{
    [Fact]
    public void EveryShortcutBelongsToADisplayedGroup()
    {
        Assert.All(KeyboardShortcuts.All, s => Assert.Contains(s.Group, KeyboardShortcuts.Groups));
    }

    [Fact]
    public void EveryGroupHasEntries()
    {
        // A heading rendered with nothing under it is a bug in the list, not a design choice.
        Assert.All(KeyboardShortcuts.Groups, g => Assert.NotEmpty(KeyboardShortcuts.InGroup(g)));
    }

    [Fact]
    public void NoGestureIsListedTwice()
    {
        var gestures = KeyboardShortcuts.All.Select(s => s.Gesture).ToList();

        Assert.Equal(gestures.Count, gestures.Distinct().Count());
    }

    [Fact]
    public void NoEntryHasBlankText()
    {
        Assert.All(KeyboardShortcuts.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Gesture));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
        });
    }

    [Theory]
    [InlineData("Ctrl+O")]
    [InlineData("Ctrl+F")]
    [InlineData("Ctrl+P")]
    [InlineData("Ctrl+E")]
    [InlineData("F1")]
    [InlineData("Ctrl+=")]
    [InlineData("Ctrl+-")]
    [InlineData("Ctrl+0")]
    [InlineData("Space")]
    public void TheShortcutsWiredInMainWindowAreAllDocumented(string gesture)
    {
        Assert.Contains(KeyboardShortcuts.All, s => s.Gesture == gesture);
    }
}
