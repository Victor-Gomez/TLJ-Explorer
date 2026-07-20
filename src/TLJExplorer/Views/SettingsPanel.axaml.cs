using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TLJExplorer.Services;
using TLJExplorer.Core.Settings;

namespace TLJExplorer.Views;

/// <summary>
/// In-app settings overlay: a Windows-Settings-style left-nav + content pane, rendered on top of
/// <see cref="MainWindow"/> rather than as a separate window. Directly mutates the shared
/// <see cref="AppSettings"/> and calls back into <see cref="MainWindow"/> for any change that
/// needs the running app to react (theme, tree filter, VFS reload).
/// </summary>
public partial class SettingsPanel : UserControl
{
    private MainWindow? _owner;
    private AppSettings? _settings;
    // Suppress Checked/Unchecked handlers while we sync controls to loaded settings, so opening
    // the panel doesn't re-persist values or trigger reloads.
    private bool _initializing;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Show the panel bound to the given host and settings, initializing controls from them.</summary>
    public void Show(MainWindow owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
        Populate();
        IsVisible = true;
        Focus();
    }

    public void Hide() => IsVisible = false;

    private void Populate()
    {
        if (_settings is null) return;

        _initializing = true;
        try
        {
            ShowMipMapsCheck.IsChecked = _settings.ShowMipMaps;
            HighQualityCheck.IsChecked = _settings.HighQuality;
            HideLocalizedCheck.IsChecked = _settings.HideLocalizedEntries;
            LoadAssetModsCheck.IsChecked = _settings.LoadAssetMods;
            DumpSceneDiagnosticsCheck.IsChecked = _settings.DumpSceneDiagnostics;

            AaCombo.SelectedIndex = _settings.AntiAliasSamples switch
            {
                0 => 1,
                2 => 2,
                4 => 3,
                _ => 0, // Default (-1)
            };

            ThemeCombo.SelectedIndex = _settings.Theme switch
            {
                "System" => 0,
                "Light" => 1,
                _ => 2, // Dark
            };

            UpdateExternalModsPathText();
            UpdateFfmpegPathText();
        }
        finally
        {
            _initializing = false;
        }
    }

    private void UpdateExternalModsPathText()
    {
        if (_settings is null) return;
        ExternalModsPathBox.Text = _settings.ExternalModsDir ?? string.Empty;
        ClearExternalModsButton.IsEnabled = !string.IsNullOrEmpty(_settings.ExternalModsDir);
    }

    private void UpdateFfmpegPathText()
    {
        if (_settings is null) return;
        FfmpegPathBox.Text = _settings.FfmpegPath ?? string.Empty;
    }

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not ListBoxItem { Tag: string tag })
            return;
        // The first ListBoxItem's IsSelected="True" fires SelectionChanged during XAML parse,
        // before the sections declared after the ListBox have been constructed.
        if (DisplaySection is null)
            return;
        DisplaySection.IsVisible     = tag == "Display";
        ModsSection.IsVisible        = tag == "Mods";
        ToolsSection.IsVisible       = tag == "Tools";
        DiagnosticsSection.IsVisible = tag == "Diagnostics";
        AppearanceSection.IsVisible  = tag == "Appearance";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Hide();

    // Click on the scrim (outside the card) closes; the card's own PointerPressed marks the
    // event handled so clicks inside don't bubble out and dismiss the panel.
    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e) => Hide();
    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void ShowMipMaps_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        _settings.ShowMipMaps = ShowMipMapsCheck.IsChecked == true;
        _settings.Save();
        _owner.OnShowMipMapsChanged();
    }

    private void HighQuality_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.HighQuality = HighQualityCheck.IsChecked == true;
        _settings.Save();
    }

    private void Aa_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        if (AaCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int samples))
        {
            _settings.AntiAliasSamples = samples;
            _settings.Save();
        }
    }

    private void HideLocalized_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        _settings.HideLocalizedEntries = HideLocalizedCheck.IsChecked == true;
        _settings.Save();
        _owner.OnHideLocalizedChanged();
    }

    private void LoadAssetMods_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        _settings.LoadAssetMods = LoadAssetModsCheck.IsChecked == true;
        _settings.Save();
        _owner.OnLoadAssetModsChanged();
    }

    private void DumpSceneDiagnostics_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.DumpSceneDiagnostics = DumpSceneDiagnosticsCheck.IsChecked == true;
        _settings.Save();
    }

    private void Theme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string theme })
        {
            _settings.Theme = theme;
            _settings.Save();
            _owner.ApplyTheme(theme);
        }
    }

    private async void SelectExternalMods_Click(object? sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        await _owner.SelectExternalModsFolderAsync();
        UpdateExternalModsPathText();
    }

    private async void ClearExternalMods_Click(object? sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        await _owner.ClearExternalModsFolderAsync();
        UpdateExternalModsPathText();
    }

    // Diagnose renders its report into MainWindow's TextViewer, so close the panel first so the
    // user actually sees the result.
    private void DiagnoseExternalMods_Click(object? sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        Hide();
        _owner.RunExternalModsDiagnostic();
    }

    private void LocateFfmpeg_Click(object? sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        _owner.PromptForFfmpeg();
        UpdateFfmpegPathText();
    }

    // Commit path edits on LostFocus / Enter. Empty text (or whitespace) unsets the value --
    // that mirrors what the Clear button did.
    private async void ExternalModsPathBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;

        string? newValue = string.IsNullOrWhiteSpace(ExternalModsPathBox.Text) ? null : ExternalModsPathBox.Text.Trim();
        if (string.Equals(newValue, _settings.ExternalModsDir, StringComparison.OrdinalIgnoreCase))
            return;

        if (newValue is not null && !Directory.Exists(newValue))
        {
            await Dialogs.ShowMessageBox(_owner,
                $"Folder not found:\n{newValue}",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateExternalModsPathText();
            return;
        }

        _settings.ExternalModsDir = newValue;
        _settings.Save();
        UpdateExternalModsPathText();
        await _owner.ReloadVfsIfLoadedAsync();
    }

    private void ExternalModsPathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ClearFocus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateExternalModsPathText(); ClearFocus(); e.Handled = true; }
    }

    private void FfmpegPathBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;

        string newValue = (FfmpegPathBox.Text ?? string.Empty).Trim();
        if (string.Equals(newValue, _settings.FfmpegPath, StringComparison.OrdinalIgnoreCase))
            return;

        // Warn (but still persist) when the file isn't there -- lets users pre-fill a planned path.
        if (newValue.Length > 0 && !File.Exists(newValue) && _owner is not null)
        {
            _ = Dialogs.ShowMessageBox(_owner,
                $"File not found:\n{newValue}\n\nSetting saved anyway; playback will fail until this path exists.",
                "TLJ Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _settings.FfmpegPath = newValue;
        _settings.Save();
        UpdateFfmpegPathText();
    }

    private void FfmpegPathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ClearFocus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateFfmpegPathText(); ClearFocus(); e.Handled = true; }
    }

    private void ClearFocus() => TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
}
