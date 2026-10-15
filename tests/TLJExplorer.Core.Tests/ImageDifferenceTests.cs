using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class ImageDifferenceTests
{
    private static DecodedImage MakeSolidImage(int w, int h, byte b, byte g, byte r, byte a)
    {
        byte[] pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        return new DecodedImage(w, h, pixels);
    }

    [Fact]
    public void ComputeDifference_NullArgs_Throws()
    {
        var img = MakeSolidImage(1, 1, 0, 0, 0, 255);
        Assert.Throws<ArgumentNullException>(() => ImageDifference.ComputeDifference(null!, img, 1, 1, 1, out _));
        Assert.Throws<ArgumentNullException>(() => ImageDifference.ComputeDifference(img, null!, 1, 1, 1, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void ComputeDifference_InvalidDimensions_Throws(int w, int h)
    {
        var img = MakeSolidImage(1, 1, 0, 0, 0, 255);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDifference.ComputeDifference(img, img, w, h, 1, out _));
    }

    [Fact]
    public void ComputeDifference_IdenticalImages_YieldsZeroDiff()
    {
        var img1 = MakeSolidImage(4, 4, 50, 100, 150, 255);
        var img2 = MakeSolidImage(4, 4, 50, 100, 150, 255);

        DecodedImage diff = ImageDifference.ComputeDifference(img1, img2, 4, 4, 1, out int differingPixels);

        Assert.Equal(0, differingPixels);
        Assert.Equal(4, diff.Width);
        Assert.Equal(4, diff.Height);

        for (int i = 0; i < diff.Pixels.Length; i += 4)
        {
            Assert.Equal(0, diff.Pixels[i]);     // B
            Assert.Equal(0, diff.Pixels[i + 1]); // G
            Assert.Equal(0, diff.Pixels[i + 2]); // R
            Assert.Equal(255, diff.Pixels[i + 3]); // A is opaque
        }
    }

    [Fact]
    public void ComputeDifference_DifferentPixels_ComputesAbsoluteDelta()
    {
        var img1 = MakeSolidImage(2, 2, 100, 150, 200, 255);
        var img2 = MakeSolidImage(2, 2, 90, 170, 200, 255);

        DecodedImage diff = ImageDifference.ComputeDifference(img1, img2, 2, 2, 1, out int differingPixels);

        Assert.Equal(4, differingPixels);
        for (int i = 0; i < diff.Pixels.Length; i += 4)
        {
            Assert.Equal(10, diff.Pixels[i]);     // |100 - 90|
            Assert.Equal(20, diff.Pixels[i + 1]); // |150 - 170|
            Assert.Equal(0, diff.Pixels[i + 2]);  // |200 - 200|
            Assert.Equal(255, diff.Pixels[i + 3]);
        }
    }

    [Fact]
    public void ComputeDifference_GainMultipliesAndClamps()
    {
        var img1 = MakeSolidImage(1, 1, 10, 20, 30, 255);
        var img2 = MakeSolidImage(1, 1, 20, 100, 30, 255);

        // Gain = 4: deltaB = 10*4=40, deltaG = 80*4=320 -> clamped to 255, deltaR = 0
        DecodedImage diff = ImageDifference.ComputeDifference(img1, img2, 1, 1, 4, out int differingPixels);

        Assert.Equal(1, differingPixels);
        Assert.Equal(40, diff.Pixels[0]);
        Assert.Equal(255, diff.Pixels[1]);
        Assert.Equal(0, diff.Pixels[2]);
        Assert.Equal(255, diff.Pixels[3]);
    }

    [Fact]
    public void ComputeDifference_BothFullyTransparent_YieldsZeroDiff()
    {
        var img1 = MakeSolidImage(2, 2, 255, 0, 0, 0);
        var img2 = MakeSolidImage(2, 2, 0, 255, 0, 0);

        DecodedImage diff = ImageDifference.ComputeDifference(img1, img2, 2, 2, 1, out int differingPixels);

        Assert.Equal(0, differingPixels);
        for (int i = 0; i < diff.Pixels.Length; i += 4)
        {
            Assert.Equal(0, diff.Pixels[i]);
            Assert.Equal(0, diff.Pixels[i + 1]);
            Assert.Equal(0, diff.Pixels[i + 2]);
            Assert.Equal(255, diff.Pixels[i + 3]);
        }
    }

    [Fact]
    public void ComputeDifference_DifferentResolutions_SamplesToCanvas()
    {
        // 1x1 image scaled to 2x2 canvas vs 2x2 image
        var img1 = MakeSolidImage(1, 1, 50, 50, 50, 255);
        var img2 = MakeSolidImage(2, 2, 50, 50, 50, 255);

        DecodedImage diff = ImageDifference.ComputeDifference(img1, img2, 2, 2, 1, out int differingPixels);

        Assert.Equal(0, differingPixels);
        Assert.Equal(2, diff.Width);
        Assert.Equal(2, diff.Height);
    }
}
