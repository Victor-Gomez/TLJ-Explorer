using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
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

        // Mirrors the original WPF DispatcherUnhandledException handler: log it, mark it handled so the
        // dispatcher doesn't propagate it into a raw crash, then shut down in an orderly fashion so
        // Window.Closed handlers run (GLFW resources in ModelRenderer dispose cleanly) instead of leaving
        // the process in a half-broken state. Environment.Exit is the fallback if something refuses to yield.
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Logger.Log($"Dispatcher.UnhandledException: {args.Exception}");
            args.Handled = true;
            try
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown(1);
            }
            catch { /* fall through to hard exit */ }
            Dispatcher.UIThread.Post(() => Environment.Exit(1), DispatcherPriority.ApplicationIdle);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log($"AppDomain.UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log($"UnobservedTaskException: {args.Exception}");
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop2)
        {
            desktop2.MainWindow = new MainWindow();
            desktop2.Exit += (_, _) => TempFiles.Cleanup();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
