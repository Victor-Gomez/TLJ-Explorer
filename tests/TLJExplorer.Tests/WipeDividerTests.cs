using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using TLJExplorer.Core.Formats;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// The wipe divider is positioned by setting Margin.Left inside a fixed-width Grid. Avalonia shrinks a
/// child's arrange slot by its own left margin, so a margin that reaches the stage's right edge squeezes
/// the divider's width to zero and it disappears instead of stopping at the edge. These pin that it
/// stays visible across the full travel.
/// </summary>
public class WipeDividerTests : UiTestBase
{
    private const int CanvasWidth = 200;
    private const int CanvasHeight = 100;

    private static DecodedImage Image(int w, int h) => new(w, h, new byte[w * h * 4]);

    private MainWindow BuildWithCompareOpen()
    {
        MainWindow window = Show(new MainWindow());

        var compare = new ImageCompareResource(
            "april.xmg", Image(CanvasWidth, CanvasHeight),
            "april.png", Image(CanvasWidth, CanvasHeight));

        typeof(MainWindow)
            .GetMethod("ShowImageCompare", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [compare]);

        Layout(window);
        return window;
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(1180, 720));
        window.Arrange(new Rect(0, 0, 1180, 720));
        window.UpdateLayout();
    }

    /// <summary>Moves the wipe to <paramref name="x"/> in stage coordinates and re-runs the layout.</summary>
    private static void SetWipeX(MainWindow window, double x)
    {
        typeof(MainWindow).GetField("_compareWipeX", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, x);
        typeof(MainWindow).GetMethod("UpdateCompareClip", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        Layout(window);
    }

    [AvaloniaTheory]
    [InlineData(0)]           // hard left
    [InlineData(1)]
    [InlineData(CanvasWidth / 2)]
    [InlineData(CanvasWidth - 1)]
    [InlineData(CanvasWidth)]  // hard right -- where it used to vanish
    [InlineData(CanvasWidth + 50)] // dragged past the edge entirely
    public void DividerStaysVisibleAcrossTheFullTravel(double wipeX)
    {
        MainWindow window = BuildWithCompareOpen();
        SetWipeX(window, wipeX);

        var divider = window.FindControl<Rectangle>("ImageCompareDivider")!;
        Assert.True(divider.Bounds.Width > 0, $"divider collapsed at x={wipeX}: {divider.Bounds}");
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(CanvasWidth)]
    [InlineData(CanvasWidth + 50)]
    public void GrabHandleStaysVisibleAtTheExtremes(double wipeX)
    {
        // The transparent hit-area matters as much as the bar: if it collapses, the divider can be
        // dragged to the edge and then never grabbed again.
        MainWindow window = BuildWithCompareOpen();
        SetWipeX(window, wipeX);

        var handle = window.FindControl<Rectangle>("ImageCompareDividerHandle")!;
        Assert.True(handle.Bounds.Width > 0, $"handle collapsed at x={wipeX}: {handle.Bounds}");
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(CanvasWidth)]
    public void DividerAndHandleStayWithinTheStageAtBothEdges(double wipeX)
    {
        MainWindow window = BuildWithCompareOpen();
        SetWipeX(window, wipeX);

        AssertContainedInStage(window, "ImageCompareDivider", wipeX);
        AssertContainedInStage(window, "ImageCompareDividerHandle", wipeX);
    }

    [AvaloniaTheory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(0.1)]
    public void ZoomedOut_TheWiderOverlaysStillStayInside(double zoom)
    {
        // Both overlays size as 1/zoom to hold a constant on-screen thickness, so zooming out is what
        // makes a centred-on-the-edge overlay hang furthest outside the canvas: at 0.1x the 14px grab
        // handle is 140 stage units wide against a 200-unit canvas.
        MainWindow window = BuildWithCompareOpen();
        typeof(MainWindow).GetField("_compareZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, zoom);
        SetWipeX(window, CanvasWidth);

        AssertContainedInStage(window, "ImageCompareDivider", CanvasWidth);
        AssertContainedInStage(window, "ImageCompareDividerHandle", CanvasWidth);
    }

    private static void AssertContainedInStage(MainWindow window, string name, double wipeX)
    {
        var stage = window.FindControl<Grid>("ImageCompareStage")!;
        var element = window.FindControl<Rectangle>(name)!;

        Assert.True(element.Bounds.Left >= -0.01,
            $"{name} at x={wipeX} overhangs the left edge: {element.Bounds}");
        Assert.True(element.Bounds.Right <= stage.Bounds.Width + 0.01,
            $"{name} at x={wipeX} overhangs stage width {stage.Bounds.Width}: {element.Bounds}");
    }
}
