using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using TLJExplorer.Core.Settings;
using TLJExplorer.Services;

namespace TLJExplorer;

public partial class App : Application
{
    /// <summary>Shared tracker for temp files; everything is deleted on exit.</summary>
    public TempFileTracker TempFiles { get; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme before the main window is created so Fluent theme resources
        // are in place when controls resolve their default brushes.
        RequestedThemeVariant = AppSettings.Load().Theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };

        // Avalonia has no WPF-style DispatcherUnhandledException hook that lets you inspect/suppress
        // a crash mid-dispatch; these two just get the exception into the log file before the process
        // goes down instead of losing it silently.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log($"AppDomain.UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log($"UnobservedTaskException: {args.Exception}");
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, _) => TempFiles.Cleanup();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
