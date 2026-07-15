using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using TLJExplorer.Views;

namespace TLJExplorer.Services;

/// <summary>Mirrors WPF's <c>MessageBoxButton</c> enum shape so call sites read the same.</summary>
public enum MessageBoxButton { OK, OKCancel, YesNo, YesNoCancel }

/// <summary>Mirrors WPF's <c>MessageBoxImage</c> enum shape so call sites read the same.</summary>
public enum MessageBoxImage { None, Information, Warning, Error, Question }

/// <summary>Mirrors WPF's <c>MessageBoxResult</c> enum shape so call sites read the same.</summary>
public enum MessageBoxResult { None, OK, Cancel, Yes, No }

/// <summary>
/// Avalonia replacements for the WPF-only dialog/clipboard APIs the app used to call directly
/// (<c>System.Windows.MessageBox</c>, <c>Microsoft.Win32.OpenFileDialog</c>/<c>SaveFileDialog</c>/
/// <c>OpenFolderDialog</c>, <c>System.Windows.Clipboard</c>). Avalonia's dialog and clipboard APIs are
/// all <see cref="Task"/>-based (there's no synchronous "block until closed" equivalent), so every call
/// site becomes <see langword="await"/>.
/// </summary>
public static class Dialogs
{
    public static Task<MessageBoxResult> ShowMessageBox(
        Window owner,
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var window = new MessageBoxWindow(message, title, button, icon);
        return window.ShowDialog<MessageBoxResult>(owner);
    }

    /// <summary>Opens a folder picker. Returns the chosen absolute path, or <see langword="null"/> if cancelled.</summary>
    public static async Task<string?> ShowOpenFolderDialog(Visual owner, string title, string? suggestedStartDirectory = null)
    {
        IStorageProvider? provider = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null)
            return null;

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = await TryGetStartFolder(provider, suggestedStartDirectory),
        };

        var result = await provider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    /// <summary>Opens a file picker. Returns the chosen absolute path, or <see langword="null"/> if cancelled.</summary>
    public static async Task<string?> ShowOpenFileDialog(
        Visual owner,
        string title,
        IReadOnlyList<FilePickerFileType>? fileTypes = null,
        string? suggestedStartDirectory = null)
    {
        IStorageProvider? provider = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null)
            return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = fileTypes,
            SuggestedStartLocation = await TryGetStartFolder(provider, suggestedStartDirectory),
        };

        var result = await provider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    /// <summary>Opens a save-file picker. Returns the chosen absolute path, or <see langword="null"/> if cancelled.</summary>
    public static async Task<string?> ShowSaveFileDialog(
        Visual owner,
        string title,
        string? suggestedFileName = null,
        IReadOnlyList<FilePickerFileType>? fileTypes = null,
        string? suggestedStartDirectory = null)
    {
        IStorageProvider? provider = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null)
            return null;

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = fileTypes,
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await TryGetStartFolder(provider, suggestedStartDirectory),
        };

        IStorageFile? file = await provider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public static Task SetClipboardTextAsync(Visual owner, string text)
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
        return clipboard is null ? Task.CompletedTask : clipboard.SetTextAsync(text);
    }

    private static Task<IStorageFolder?> TryGetStartFolder(IStorageProvider provider, string? directory) =>
        string.IsNullOrEmpty(directory) ? Task.FromResult<IStorageFolder?>(null) : provider.TryGetFolderFromPathAsync(directory);
}
