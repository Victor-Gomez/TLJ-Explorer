using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Pins the compare view's mode picker. The two segments used to be grouped RadioButtons whose
/// <c>Checked</c> handler read the *other* button's <c>IsChecked</c> -- but Avalonia raises
/// <c>Checked</c> before the group unchecks the siblings, so the handler always saw the outgoing
/// selection and "Side-by-side" never took effect (see <see cref="RadioButtonGroupOrderingTests"/>).
/// </summary>
public class ImageCompareModeTests : UiTestBase
{
    private (MainWindow Window, ToggleButton Wipe, ToggleButton Side, Control WipeView, Control SideView) Build()
    {
        MainWindow window = Track(new MainWindow());
        return (
            window,
            window.FindControl<ToggleButton>("ImageCompareWipeMode")!,
            window.FindControl<ToggleButton>("ImageCompareSideBySideMode")!,
            window.FindControl<Control>("ImageCompareWipeScroll")!,
            window.FindControl<Control>("ImageCompareSideBySideContainer")!);
    }

    /// <summary>Invokes the private Click handler exactly as the XAML binding does.</summary>
    private static void ClickSegment(MainWindow window, ToggleButton segment)
    {
        // A real click toggles IsChecked before the handler runs; mirror that so the handler is
        // exercised under the same state a user click produces.
        segment.IsChecked = segment.IsChecked != true;

        MethodInfo handler = typeof(MainWindow).GetMethod(
            "ImageCompareMode_Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(window, [segment, new RoutedEventArgs()]);
    }

    [AvaloniaFact]
    public void SelectingSideBySide_SwitchesTheVisibleView()
    {
        (MainWindow window, ToggleButton wipe, ToggleButton side, Control wipeView, Control sideView) = Build();
        window.FindControl<Control>("ImageComparePanel")!.IsVisible = true;

        ClickSegment(window, side);

        Assert.True(side.IsChecked);
        Assert.False(wipe.IsChecked);
        Assert.True(sideView.IsVisible);
        Assert.False(wipeView.IsVisible);
    }

    [AvaloniaFact]
    public void SelectingWipeAgain_SwitchesBack()
    {
        (MainWindow window, ToggleButton wipe, ToggleButton side, Control wipeView, Control sideView) = Build();
        window.FindControl<Control>("ImageComparePanel")!.IsVisible = true;

        ClickSegment(window, side);
        ClickSegment(window, wipe);

        Assert.True(wipe.IsChecked);
        Assert.False(side.IsChecked);
        Assert.True(wipeView.IsVisible);
        Assert.False(sideView.IsVisible);
    }

    [AvaloniaFact]
    public void ClickingTheAlreadyActiveSegment_KeepsItSelected()
    {
        // A ToggleButton un-toggles itself on a second click; the picker must not end up with neither
        // segment selected, the way a radio group would never allow.
        (MainWindow window, ToggleButton wipe, ToggleButton side, Control wipeView, _) = Build();
        window.FindControl<Control>("ImageComparePanel")!.IsVisible = true;

        ClickSegment(window, wipe);
        ClickSegment(window, wipe);

        Assert.True(wipe.IsChecked);
        Assert.False(side.IsChecked);
        Assert.True(wipeView.IsVisible);
    }
}

/// <summary>
/// Documents the Avalonia behaviour behind the bug above, so the reasoning survives even if the compare
/// view is rewritten: a RadioButton's <c>Checked</c> event fires while its group siblings are still
/// checked. Any handler reading a sibling's state from inside <c>Checked</c> reads stale state.
/// </summary>
public class RadioButtonGroupOrderingTests : UiTestBase
{
    [AvaloniaFact]
    public void CheckedFiresBeforeSiblingsAreUnchecked()
    {
        var first = new RadioButton { GroupName = "g", IsChecked = true };
        var second = new RadioButton { GroupName = "g" };
        Show(new Window { Content = new StackPanel { Children = { first, second } } });

        bool? siblingSeenFromInsideHandler = null;
#pragma warning disable CS0618 // Checked is obsolete, but it is the event the compare view's XAML used
                              // and whose ordering caused the bug -- that is precisely what's under test.
        second.Checked += (_, _) => siblingSeenFromInsideHandler = first.IsChecked;
#pragma warning restore CS0618

        second.IsChecked = true;

        Assert.True(siblingSeenFromInsideHandler);  // stale: still checked during the event
        Assert.False(first.IsChecked);              // settled only afterwards
    }
}

/// <summary>
/// Checks the segmented picker actually paints its selected segment. The style resolves brush
/// resources through DynamicResource, and a key that doesn't resolve (or resolves to a Color rather
/// than a Brush) fails silently at runtime, leaving both segments looking identical.
/// </summary>
public class SegmentedPickerStyleTests : UiTestBase
{
    [AvaloniaFact]
    public void OnlyTheSelectedSegmentIsFilled()
    {
        MainWindow window = Show(new MainWindow());
        window.FindControl<Control>("ImageComparePanel")!.IsVisible = true;
        window.Measure(new Size(1180, 720));
        window.Arrange(new Rect(0, 0, 1180, 720));

        var wipe = window.FindControl<ToggleButton>("ImageCompareWipeMode")!;
        var side = window.FindControl<ToggleButton>("ImageCompareSideBySideMode")!;

        Assert.True(wipe.IsChecked);

        // The selected segment gets the accent fill; the unselected one keeps the transparent base.
        var selected = Assert.IsAssignableFrom<ISolidColorBrush>(wipe.Background);
        Assert.Equal(Color.Parse("#FF60CDFF"), selected.Color);

        var unselected = Assert.IsAssignableFrom<ISolidColorBrush>(side.Background);
        Assert.Equal(Colors.Transparent, unselected.Color);
    }

    [AvaloniaFact]
    public void TheFillFollowsTheSelection()
    {
        MainWindow window = Show(new MainWindow());
        window.FindControl<Control>("ImageComparePanel")!.IsVisible = true;
        window.Measure(new Size(1180, 720));
        window.Arrange(new Rect(0, 0, 1180, 720));

        var wipe = window.FindControl<ToggleButton>("ImageCompareWipeMode")!;
        var side = window.FindControl<ToggleButton>("ImageCompareSideBySideMode")!;

        wipe.IsChecked = false;
        side.IsChecked = true;
        window.Measure(new Size(1180, 720));
        window.Arrange(new Rect(0, 0, 1180, 720));

        Assert.Equal(Color.Parse("#FF60CDFF"), Assert.IsAssignableFrom<ISolidColorBrush>(side.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(wipe.Background).Color);
    }
}
