using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TLJExplorer.Services;

/// <summary>
/// One-way <see cref="IValueConverter"/> that turns a string resource key (e.g. <c>"ImageTypeIcon"</c>)
/// into the corresponding <see cref="IImage"/> resolved via <see cref="Application.TryFindResource"/>.
/// Used to bind icons to items in a data-templated <see cref="ComboBox"/> where the resource key comes
/// from the item itself.
/// </summary>
public sealed class ResourceKeyToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return null;

        return Application.Current is { } app && app.TryFindResource(key, out object? resource)
            ? resource as IImage
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
