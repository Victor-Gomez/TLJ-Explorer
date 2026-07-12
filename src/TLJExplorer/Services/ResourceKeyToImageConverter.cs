using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TLJExplorer.Services;

/// <summary>
/// One-way <see cref="IValueConverter"/> that turns a string resource key (e.g. <c>"ImageTypeIcon"</c>)
/// into the corresponding <see cref="ImageSource"/> resolved via <see cref="Application.TryFindResource"/>.
/// Used to bind icons to items in a data-templated <see cref="System.Windows.Controls.ComboBox"/> where
/// the resource key comes from the item itself.
/// </summary>
public sealed class ResourceKeyToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return null;

        return Application.Current?.TryFindResource(key) as ImageSource;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
