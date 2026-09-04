using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TLJExplorer.Services;

namespace TLJExplorer.Views;

/// <summary>
/// Help &gt; Keyboard Shortcuts (F1). Built from <see cref="KeyboardShortcuts.All"/> at runtime rather
/// than hand-written in XAML, so the list and the data other code can assert against cannot diverge.
/// </summary>
public sealed partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        BuildRows();

        KeyDown += (_, e) =>
        {
            // F1 closes as well as opens: pressing it again is the natural way to dismiss a cheat sheet.
            if (e.Key is Key.Escape or Key.F1)
                Close();
        };
    }

    private void BuildRows()
    {
        foreach (string group in KeyboardShortcuts.Groups)
        {
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = group,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, GroupsPanel.Children.Count == 0 ? 0 : 16, 0, 6),
            });

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,*"),
            };

            int row = 0;
            foreach (KeyboardShortcut shortcut in KeyboardShortcuts.InGroup(group))
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                var gesture = new Border
                {
                    // A bordered chip reads as "a key" at a glance, which a plain string doesn't.
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 1),
                    Margin = new Thickness(0, 2, 12, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = shortcut.Gesture,
                        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                        FontSize = 11.5,
                    },
                };

                var description = new TextBlock
                {
                    Text = shortcut.Description,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.85,
                };

                Grid.SetRow(gesture, row);
                Grid.SetColumn(gesture, 0);
                Grid.SetRow(description, row);
                Grid.SetColumn(description, 1);
                grid.Children.Add(gesture);
                grid.Children.Add(description);
                row++;
            }

            GroupsPanel.Children.Add(grid);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
