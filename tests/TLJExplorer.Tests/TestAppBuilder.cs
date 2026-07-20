using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(TLJExplorer.Tests.TestAppBuilder))]

namespace TLJExplorer.Tests;

/// <summary>
/// Entry point for <c>[AvaloniaFact]</c>/<c>[AvaloniaTheory]</c> tests. A dedicated minimal
/// <see cref="Application"/> rather than the real <c>TLJExplorer.App</c>: the real app's
/// <c>OnFrameworkInitializationCompleted</c> creates a full <c>MainWindow</c> (VFS, LibVLC, settings
/// I/O), which is far more than a unit test needs. This merges the same FluentTheme + VectorIcons
/// resources the real app does, so tests that resolve icon resources (see
/// <c>ResourceKeyToImageConverterTests</c>) see the real thing.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://TLJExplorer/Assets/Icons/VectorIcons.axaml"),
        });
    }
}
