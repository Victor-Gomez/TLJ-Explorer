using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        Visibility = Visibility.Visible;
        Focus();
    }

    public void Hide() => Visibility = Visibility.Collapsed;

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

            RadioButton aa = _settings.AntiAliasSamples switch
            {
                0 => AaNoneRadio,
                2 => Aa2xRadio,
                4 => Aa4xRadio,
                _ => AaDefaultRadio,
            };
            aa.IsChecked = true;

            RadioButton theme = _settings.Theme switch
            {
                "System" => ThemeSystemRadio,
                "Light" => ThemeLightRadio,
                _ => ThemeDarkRadio,
            };
            theme.IsChecked = true;

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
        bool has = !string.IsNullOrEmpty(_settings.ExternalModsDir);
        ExternalModsPathText.Text = has ? _settings.ExternalModsDir : "(none)";
        ClearExternalModsButton.IsEnabled = has;
    }

    private void UpdateFfmpegPathText()
    {
        if (_settings is null) return;
        FfmpegPathText.Text = string.IsNullOrEmpty(_settings.FfmpegPath) ? "(not set)" : _settings.FfmpegPath;
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not ListBoxItem { Tag: string tag })
            return;
        // The first ListBoxItem's IsSelected="True" fires SelectionChanged during XAML parse,
        // before the sections declared after the ListBox have been constructed.
        if (DisplaySection is null)
            return;
        DisplaySection.Visibility     = tag == "Display"     ? Visibility.Visible : Visibility.Collapsed;
        ModsSection.Visibility        = tag == "Mods"        ? Visibility.Visible : Visibility.Collapsed;
        ToolsSection.Visibility       = tag == "Tools"       ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsSection.Visibility = tag == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility  = tag == "Appearance"  ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // Click on the scrim (outside the card) closes; the card's own MouseLeftButtonDown marks the
    // event handled so clicks inside don't bubble out and dismiss the panel.
    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Hide();
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ShowMipMaps_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        _settings.ShowMipMaps = ShowMipMapsCheck.IsChecked == true;
        _settings.Save();
        _owner.OnShowMipMapsChanged();
    }

    private void HighQuality_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.HighQuality = HighQualityCheck.IsChecked == true;
        _settings.Save();
    }

    private void Aa_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int samples))
        {
            _settings.AntiAliasSamples = samples;
            _settings.Save();
        }
    }

    private void HideLocalized_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        _settings.HideLocalizedEntries = HideLocalizedCheck.IsChecked == true;
        _settings.Save();
        _owner.OnHideLocalizedChanged();
    }

    private void LoadAssetMods_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        _settings.LoadAssetMods = LoadAssetModsCheck.IsChecked == true;
        _settings.Save();
        _owner.OnLoadAssetModsChanged();
    }

    private void DumpSceneDiagnostics_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.DumpSceneDiagnostics = DumpSceneDiagnosticsCheck.IsChecked == true;
        _settings.Save();
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null || _owner is null) return;
        if (sender is RadioButton { Tag: string theme })
        {
            _settings.Theme = theme;
            _settings.Save();
            _owner.ApplyTheme(theme);
        }
    }

    private async void SelectExternalMods_Click(object sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        await _owner.SelectExternalModsFolderAsync();
        UpdateExternalModsPathText();
    }

    private async void ClearExternalMods_Click(object sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        await _owner.ClearExternalModsFolderAsync();
        UpdateExternalModsPathText();
    }

    // Diagnose renders its report into MainWindow's TextViewer, so close the panel first so the
    // user actually sees the result.
    private void DiagnoseExternalMods_Click(object sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        Hide();
        _owner.RunExternalModsDiagnostic();
    }

    private void LocateFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (_owner is null) return;
        _owner.PromptForFfmpeg();
        UpdateFfmpegPathText();
    }
}
