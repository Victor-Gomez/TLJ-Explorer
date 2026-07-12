using System.Windows;
using System.Windows.Threading;
using TLJExplorer.Services;
using TLJExplorer.Core.Settings;

namespace TLJExplorer;

/// <summary>
/// Interaction logic for App.xaml.
/// </summary>
public partial class App : Application
{
    /// <summary>Shared tracker for temp files; everything is deleted on exit.</summary>
    public TempFileTracker TempFiles { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the persisted theme before the main window is created so Fluent theme resources
        // are in place when controls resolve their default brushes.
#pragma warning disable WPF0001 // ThemeMode is marked "evaluation only"; re-surface if API changes.
        ThemeMode = AppSettings.Load().Theme switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark,
        };
#pragma warning restore WPF0001

        base.OnStartup(e);

        // Report unhandled exceptions instead of silently killing the process. Log file location is
        // Logger.LogFilePath.
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Log($"DispatcherUnhandledException: {args.Exception}");
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception}",
                "TLJExplorer - Unhandled Exception",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log($"AppDomain.UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log($"UnobservedTaskException: {args.Exception}");
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TempFiles.Cleanup();
        base.OnExit(e);
    }
}
