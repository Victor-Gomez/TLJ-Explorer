using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class DecodedImageTests
{
    [Fact]
    public void Constructor_ValidBuffer_SetsProperties()
    {
        var pixels = new byte[2 * 3 * 4];
        var image = new DecodedImage(2, 3, pixels);

        Assert.Equal(2, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Same(pixels, image.Pixels);
    }

    [Fact]
    public void Constructor_ZeroDimensions_AllowsEmptyBuffer()
    {
        var image = new DecodedImage(0, 0, []);

        Assert.Equal(0, image.Width);
        Assert.Equal(0, image.Height);
        Assert.Empty(image.Pixels);
    }

    [Fact]
    public void Constructor_NegativeWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedImage(-1, 1, new byte[4]));
    }

    [Fact]
    public void Constructor_NegativeHeight_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedImage(1, -1, new byte[4]));
    }

    [Fact]
    public void Constructor_NullPixels_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DecodedImage(1, 1, null!));
    }

    [Fact]
    public void Constructor_MismatchedPixelLength_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new DecodedImage(2, 2, new byte[4]));
        Assert.Contains("Width*Height*4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalProperties_DefaultToNull()
    {
        var image = new DecodedImage(1, 1, new byte[4]);

        Assert.Null(image.Name);
        Assert.Null(image.TransparentColorBgr);
    }

    [Fact]
    public void OptionalProperties_CanBeSetViaInitializer()
    {
        var image = new DecodedImage(1, 1, new byte[4])
        {
            Name = "sprite01",
            TransparentColorBgr = 0x00FF00,
        };

        Assert.Equal("sprite01", image.Name);
        Assert.Equal(0x00FF00u, image.TransparentColorBgr);
    }
}
