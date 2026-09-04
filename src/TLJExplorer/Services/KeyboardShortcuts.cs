namespace TLJExplorer.Services;

/// <summary>One documented shortcut: the keys, what it does, and which part of the app it applies to.</summary>
public readonly record struct KeyboardShortcut(string Group, string Gesture, string Description);

/// <summary>
/// The shortcut list shown by Help &gt; Keyboard Shortcuts (F1). Kept as data rather than baked into the
/// window's XAML so it can be asserted against in tests -- every one of these is handled in
/// <c>MainWindow.MainWindow_PreviewKeyDown</c>, and a list that drifts from the handler is worse than no
/// list at all.
/// </summary>
public static class KeyboardShortcuts
{
    public const string GeneralGroup = "General";
    public const string PreviewGroup = "Preview";
    public const string PlaybackGroup = "Playback";

    public static readonly IReadOnlyList<KeyboardShortcut> All =
    [
        new(GeneralGroup, "Ctrl+O", "Select TLJ install folder"),
        new(GeneralGroup, "Ctrl+F", "Jump to the tree search box"),
        new(GeneralGroup, "Ctrl+P", "Command palette (find a file by name)"),
        new(GeneralGroup, "Ctrl+E", "Export the selected item"),
        new(GeneralGroup, "Ctrl+,", "Settings"),
        new(GeneralGroup, "F1", "This shortcut list"),
        new(GeneralGroup, "Esc", "Close the settings overlay"),

        new(PreviewGroup, "Ctrl+=", "Zoom in (image and scene views)"),
        new(PreviewGroup, "Ctrl+-", "Zoom out"),
        new(PreviewGroup, "Ctrl+0", "Fit to window"),
        new(PreviewGroup, "Mouse wheel", "Zoom the image, scene or compare view"),
        new(PreviewGroup, "Left-drag", "Pan a zoomed image, scene or compare view"),

        new(PlaybackGroup, "Space", "Play / pause the sound player, or the model animation"),
        new(PlaybackGroup, "Left / Right", "Seek the sound player by 5 seconds"),
    ];

    /// <summary>The shortcuts belonging to <paramref name="group"/>, in declared order.</summary>
    public static IEnumerable<KeyboardShortcut> InGroup(string group) =>
        All.Where(s => s.Group == group);

    /// <summary>Group names in the order they should be displayed.</summary>
    public static IReadOnlyList<string> Groups => [GeneralGroup, PreviewGroup, PlaybackGroup];
}
