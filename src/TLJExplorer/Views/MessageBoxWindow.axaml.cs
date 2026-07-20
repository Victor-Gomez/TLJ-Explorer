using Avalonia.Controls;
using Avalonia.Input;
using TLJExplorer.Services;

namespace TLJExplorer.Views;

/// <summary>
/// Minimal in-house replacement for WPF's <c>System.Windows.MessageBox</c> (Avalonia ships no built-in
/// equivalent). Built rather than pulling in a third-party package to keep the same "no extra UI
/// dependencies" footprint the WPF app had.
/// </summary>
public sealed partial class MessageBoxWindow : Window
{
    // Designer/XAML-loader parameterless constructor only; always use the other constructor at runtime.
    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    public MessageBoxWindow(string message, string title, MessageBoxButton button, MessageBoxImage icon) : this()
    {
        Title = title;
        MessageText.Text = message;
        IconText.Text = IconGlyph(icon);
        IconText.IsVisible = IconText.Text is not null;

        MessageBoxResult defaultResult = AddButtons(button);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(button is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel ? MessageBoxResult.Cancel : defaultResult);
        };
    }

    private MessageBoxResult AddButtons(MessageBoxButton button)
    {
        (string Label, MessageBoxResult Result)[] specs = button switch
        {
            MessageBoxButton.OKCancel => [("Cancel", MessageBoxResult.Cancel), ("OK", MessageBoxResult.OK)],
            MessageBoxButton.YesNo => [("No", MessageBoxResult.No), ("Yes", MessageBoxResult.Yes)],
            MessageBoxButton.YesNoCancel => [("Cancel", MessageBoxResult.Cancel), ("No", MessageBoxResult.No), ("Yes", MessageBoxResult.Yes)],
            _ => [("OK", MessageBoxResult.OK)],
        };

        foreach ((string label, MessageBoxResult result) in specs)
        {
            var btn = new Button { Content = label, Padding = new Avalonia.Thickness(16, 6), MinWidth = 72 };
            btn.Click += (_, _) => Close(result);
            ButtonPanel.Children.Add(btn);
        }

        // Last button added is the visually right-most/primary one -- give it focus so Enter activates it.
        MessageBoxResult defaultResult = specs[^1].Result;
        Opened += (_, _) => (ButtonPanel.Children[^1] as Button)?.Focus();
        return defaultResult;
    }

    private static string? IconGlyph(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Information => "i",
        MessageBoxImage.Warning => "!",
        MessageBoxImage.Error => "x",
        MessageBoxImage.Question => "?",
        _ => null,
    };
}
