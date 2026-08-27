using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TLJExplorer.Core.Settings;
using TLJExplorer.Services;

namespace TLJExplorer.Views;

/// <summary>
/// Help &gt; About. Doubles as the app's diagnostics panel: alongside the version and credits it reports
/// whether ffmpeg and LibVLC actually resolved, since those two external dependencies are the usual
/// reason a preview silently does nothing, and "Copy Diagnostics" puts the lot on the clipboard for a
/// bug report.
/// </summary>
public sealed partial class AboutWindow : Window
{
    private readonly AppSettings _settings;

    // Designer/XAML-loader parameterless constructor only; always use the other constructor at runtime.
    public AboutWindow() : this(new AppSettings())
    {
    }

    public AboutWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        VersionText.Text = AppInfo.CommitSha is { } sha
            ? $"Version {AppInfo.DisplayVersion} ({sha})"
            : $"Version {AppInfo.InformationalVersion}";

        RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        AvaloniaText.Text = AppInfo.AvaloniaVersion;
        FfmpegText.Text = AppInfo.DescribeFfmpeg(_settings);
        LogPathText.Text = Log.FilePath;

        // Probing LibVLC initialises the native engine, which takes a couple hundred ms. Show a
        // placeholder and fill it in once the window is up rather than stalling the open.
        LibVlcText.Text = "Checking...";
        Opened += async (_, _) =>
        {
            string status = await Task.Run(AppInfo.DescribeLibVlc);
            LibVlcText.Text = status;
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenRepo_Click(object? sender, RoutedEventArgs e) =>
        SystemBrowser.TryOpen(RepositoryUrls.Home(_settings.ReleaseFeedUrl));

    private void OpenIssues_Click(object? sender, RoutedEventArgs e) =>
        SystemBrowser.TryOpen(RepositoryUrls.Issues(_settings.ReleaseFeedUrl));

    private void OpenReleases_Click(object? sender, RoutedEventArgs e) =>
        SystemBrowser.TryOpen(RepositoryUrls.Releases(_settings.ReleaseFeedUrl));

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Checking...";
        try
        {
            await UpdateUi.CheckInteractiveAsync(this, _settings);
        }
        finally
        {
            CheckUpdatesButton.Content = "Check for Updates";
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        var report = new StringBuilder()
            .AppendLine($"TLJ Explorer {AppInfo.InformationalVersion}")
            .AppendLine($".NET:     {RuntimeText.Text}")
            .AppendLine($"Avalonia: {AvaloniaText.Text}")
            .AppendLine($"ffmpeg:   {FfmpegText.Text}")
            .AppendLine($"LibVLC:   {LibVlcText.Text}")
            .AppendLine($"Log:      {Log.FilePath}")
            .ToString();

        await Dialogs.SetClipboardTextAsync(this, report);
    }
}
