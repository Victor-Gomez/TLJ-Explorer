using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using TLJExplorer.Core.Settings;
using TLJExplorer.Services;
using TLJExplorer.Views;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Loads the real About window against the real theme. XAML faults -- an unresolvable DynamicResource,
/// a mistyped x:Name -- only ever surface when the window is actually constructed, so a compiling build
/// proves nothing on its own.
/// </summary>
public class AboutWindowTests : UiTestBase
{
    // x:Name fields are internal to the UI assembly, so reach the controls through the name scope.
    private static string? Text(Window window, string name) =>
        window.FindControl<SelectableTextBlock>(name)?.Text;

    [AvaloniaFact]
    public void Construct_LoadsTheXamlAndFillsInTheVersion()
    {
        AboutWindow window = Track(new AboutWindow(new AppSettings()));

        Assert.Contains(AppInfo.DisplayVersion, Text(window, "VersionText")!);
    }

    [AvaloniaFact]
    public void Construct_ReportsMissingFfmpegRatherThanFailing()
    {
        var settings = new AppSettings { FfmpegPath = "/nonexistent/ffmpeg" };

        AboutWindow window = Track(new AboutWindow(settings));

        Assert.StartsWith("Missing:", Text(window, "FfmpegText")!);
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Links_AreLegibleInBothThemes(string themeName)
    {
        // Regression: the links were originally styled with DynamicResource
        // AccentTextFillColorPrimaryBrush, a key the Fluent theme does not define. The setter silently
        // did nothing and every link rendered black -- invisible against the dark background.
        ThemeVariant variant = themeName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = variant;

        AboutWindow window = Show(new AboutWindow(new AppSettings()));
        window.Measure(new Size(520, 2000));
        window.Arrange(new Rect(0, 0, 520, 2000));

        var links = window.GetLogicalDescendants().OfType<HyperlinkButton>().ToList();
        Assert.Equal(3, links.Count);

        foreach (HyperlinkButton link in links)
        {
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(link.Foreground);
            Assert.True(
                Contrast(brush.Color, variant == ThemeVariant.Dark ? Colors.Black : Colors.White) >= 4.5,
                $"'{link.Content}' contrast too low in {themeName}: {brush.Color}");
        }
    }

    /// <summary>WCAG relative-luminance contrast ratio between two opaque colors.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        (double hi, double lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    [AvaloniaFact]
    public void Construct_ReportsFfmpegAsUnconfiguredWhenThePathIsBlank()
    {
        AboutWindow window = Track(new AboutWindow(new AppSettings { FfmpegPath = "" }));

        Assert.Contains("Not configured", Text(window, "FfmpegText")!);
    }
}
