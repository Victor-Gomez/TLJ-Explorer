using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Exercises the Phase 1 Avalonia port of the type-filter combo's icon converter against the real
/// merged VectorIcons.axaml resources (see <see cref="TestAppBuilder"/>), not a mock resource host.
/// </summary>
public class ResourceKeyToImageConverterTests
{
    private static readonly ResourceKeyToImageConverter Converter = new();

    [AvaloniaFact]
    public void Convert_KnownIconKey_ResolvesToRealIcon()
    {
        object? result = Converter.Convert("ImageTypeIcon", typeof(IImage), null, CultureInfo.InvariantCulture);

        Assert.IsAssignableFrom<IImage>(result);
    }

    [AvaloniaFact]
    public void Convert_UnknownKey_ReturnsNull()
    {
        object? result = Converter.Convert("NoSuchIconKeyAtAll", typeof(IImage), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    [AvaloniaFact]
    public void Convert_NullValue_ReturnsNull()
    {
        object? result = Converter.Convert(null, typeof(IImage), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    [AvaloniaFact]
    public void Convert_EmptyString_ReturnsNull()
    {
        object? result = Converter.Convert(string.Empty, typeof(IImage), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    [AvaloniaFact]
    public void Convert_NonStringValue_ReturnsNull()
    {
        object? result = Converter.Convert(42, typeof(IImage), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    [AvaloniaFact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            Converter.ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
