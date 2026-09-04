using Avalonia;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Restoring a window to bounds saved under a different display layout is how apps end up invisible off
/// the side of the desktop -- and on Windows an off-screen window is effectively unrecoverable without
/// hand-editing settings. These pin the rule that decides when to fall back to centring.
/// </summary>
public class WindowGeometryTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect SecondaryLeft = new(-1920, 0, 1920, 1080);

    [Fact]
    public void BoundsFullyOnAScreen_AreRestorable()
    {
        Assert.True(WindowGeometry.IsRestorable(new PixelRect(100, 100, 1180, 720), [Primary]));
    }

    [Fact]
    public void BoundsOnASecondMonitorWithNegativeCoordinates_AreRestorable()
    {
        // A monitor arranged to the left of the primary has negative X; that's normal, not corrupt.
        Assert.True(WindowGeometry.IsRestorable(new PixelRect(-1800, 50, 1180, 720), [Primary, SecondaryLeft]));
    }

    [Fact]
    public void BoundsOnAMonitorThatIsNoLongerAttached_AreNotRestorable()
    {
        Assert.False(WindowGeometry.IsRestorable(new PixelRect(-1800, 50, 1180, 720), [Primary]));
    }

    [Fact]
    public void BoundsFarBelowTheDesktop_AreNotRestorable()
    {
        Assert.False(WindowGeometry.IsRestorable(new PixelRect(100, 5000, 1180, 720), [Primary]));
    }

    [Fact]
    public void BoundsOverlappingByOnlyASliver_AreNotRestorable()
    {
        // 10px of the window on screen is not a grabbable title bar.
        Assert.False(WindowGeometry.IsRestorable(new PixelRect(1910, 100, 1180, 720), [Primary]));
    }

    [Fact]
    public void BoundsHangingOffTheEdgeButStillGrabbable_AreRestorable()
    {
        Assert.True(WindowGeometry.IsRestorable(new PixelRect(1600, 100, 1180, 720), [Primary]));
    }

    [Fact]
    public void AbsurdlySmallBounds_AreRejectedAsCorrupt()
    {
        Assert.False(WindowGeometry.IsRestorable(new PixelRect(100, 100, 20, 20), [Primary]));
    }

    [Fact]
    public void NoScreensReported_MeansNothingIsRestorable()
    {
        Assert.False(WindowGeometry.IsRestorable(new PixelRect(100, 100, 1180, 720), []));
    }

    [Theory]
    [InlineData(null, 10, 800.0, 600.0)]
    [InlineData(10, null, 800.0, 600.0)]
    [InlineData(10, 10, null, 600.0)]
    [InlineData(10, 10, 800.0, null)]
    public void PartiallyMissingSettings_YieldNoCandidate(int? x, int? y, double? w, double? h)
    {
        Assert.Null(WindowGeometry.FromSettings(x, y, w, h));
    }

    [Theory]
    [InlineData(double.NaN, 600.0)]
    [InlineData(800.0, double.NaN)]
    [InlineData(0.0, 600.0)]
    [InlineData(-100.0, 600.0)]
    public void NonsenseSizesInSettings_YieldNoCandidate(double w, double h)
    {
        // A hand-edited or partially-written settings.json must not become a garbage rect.
        Assert.Null(WindowGeometry.FromSettings(10, 10, w, h));
    }

    [Fact]
    public void CompleteSettings_RoundTripIntoARect()
    {
        PixelRect? rect = WindowGeometry.FromSettings(15, 25, 1180.0, 720.0);

        Assert.Equal(new PixelRect(15, 25, 1180, 720), rect);
    }
}
