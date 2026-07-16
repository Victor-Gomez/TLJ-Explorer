using Avalonia.Headless.XUnit;
using TLJExplorer.Core.Formats;
using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

/// <summary>
/// Exercises the Phase 1 Avalonia port of PNG encode/decode (<see cref="PngWriter"/>,
/// <see cref="PngToDecodedImage"/>), which replaced WPF's PngBitmapEncoder/Decoder. These need a real
/// Skia backend (not the stub headless renderer), see <see cref="TestAppBuilder"/>.
/// </summary>
public class PngRoundTripTests
{
    /// <summary>4x4 BGRA32 image: a 2x2 checkerboard of opaque red/blue plus a fully transparent pixel
    /// and a half-transparent pixel, so both dimensions and alpha survive the round trip get checked.</summary>
    private static DecodedImage BuildTestImage()
    {
        const int w = 4, h = 4;
        var pixels = new byte[w * h * 4];

        void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
        {
            int o = ((y * w) + x) * 4;
            pixels[o + 0] = b;
            pixels[o + 1] = g;
            pixels[o + 2] = r;
            pixels[o + 3] = a;
        }

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            SetPixel(x, y, 0, 0, 255, 255); // opaque red background

        SetPixel(0, 0, 255, 0, 0, 255); // opaque blue
        SetPixel(1, 1, 0, 0, 0, 0); // fully transparent
        SetPixel(2, 2, 0, 255, 0, 128); // half-transparent green

        return new DecodedImage(w, h, pixels);
    }

    [AvaloniaFact]
    public void WriteThenDecode_PreservesDimensions()
    {
        DecodedImage original = BuildTestImage();
        using var stream = new MemoryStream();

        PngWriter.Write(original, stream);
        stream.Position = 0;
        DecodedImage decoded = PngToDecodedImage.Decode(stream);

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
    }

    [AvaloniaFact]
    public void WriteThenDecode_PreservesOpaquePixelColor()
    {
        DecodedImage original = BuildTestImage();
        using var stream = new MemoryStream();

        PngWriter.Write(original, stream);
        stream.Position = 0;
        DecodedImage decoded = PngToDecodedImage.Decode(stream);

        // (0,0) was set to opaque blue (B=255,G=0,R=0,A=255).
        Assert.Equal(255, decoded.Pixels[0]);
        Assert.Equal(0, decoded.Pixels[1]);
        Assert.Equal(0, decoded.Pixels[2]);
        Assert.Equal(255, decoded.Pixels[3]);
    }

    [AvaloniaFact]
    public void WriteThenDecode_PreservesFullTransparency()
    {
        DecodedImage original = BuildTestImage();
        using var stream = new MemoryStream();

        PngWriter.Write(original, stream);
        stream.Position = 0;
        DecodedImage decoded = PngToDecodedImage.Decode(stream);

        // (1,1) was fully transparent -- alpha must round-trip as 0 regardless of RGB.
        int o = ((1 * decoded.Width) + 1) * 4;
        Assert.Equal(0, decoded.Pixels[o + 3]);
    }

    [AvaloniaFact]
    public void WriteThenDecode_PreservesPartialTransparency()
    {
        DecodedImage original = BuildTestImage();
        using var stream = new MemoryStream();

        PngWriter.Write(original, stream);
        stream.Position = 0;
        DecodedImage decoded = PngToDecodedImage.Decode(stream);

        // (2,2) was set to alpha=128 (green, half-transparent).
        int o = ((2 * decoded.Width) + 2) * 4;
        Assert.Equal(128, decoded.Pixels[o + 3]);
    }

    [Fact]
    public void LooksLikePng_RecognizesSignature()
    {
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
        Assert.True(PngToDecodedImage.LooksLikePng(pngSignature));
    }

    [Fact]
    public void LooksLikePng_RejectsOtherData()
    {
        Assert.False(PngToDecodedImage.LooksLikePng([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void LooksLikePng_RejectsTooShortInput()
    {
        Assert.False(PngToDecodedImage.LooksLikePng([0x89, 0x50]));
    }

    [AvaloniaFact]
    public void DecodeXmgOrPng_SniffsPngAndDecodesIt()
    {
        DecodedImage original = BuildTestImage();
        using var stream = new MemoryStream();
        PngWriter.Write(original, stream);
        stream.Position = 0;

        bool fallbackCalled = false;
        DecodedImage decoded = PngToDecodedImage.DecodeXmgOrPng(stream, _ =>
        {
            fallbackCalled = true;
            return original;
        });

        Assert.False(fallbackCalled);
        Assert.Equal(original.Width, decoded.Width);
    }

    [Fact]
    public void DecodeXmgOrPng_NonPngStream_CallsFallback()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        DecodedImage sentinel = new(1, 1, new byte[4]);

        DecodedImage decoded = PngToDecodedImage.DecodeXmgOrPng(stream, _ => sentinel);

        Assert.Same(sentinel, decoded);
    }
}
