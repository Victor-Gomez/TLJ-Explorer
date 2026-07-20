using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

/// <summary>
/// NVorbis has no bundled encoder, so there's no way to synthesize a valid Ogg Vorbis stream for a
/// true decode round trip here — only the argument/error-path contract is covered.
/// </summary>
public class OggDecoderTests
{
    [Fact]
    public void DecodeToWav_NullInput_Throws()
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => OggDecoder.DecodeToWav(null!, output));
    }

    [Fact]
    public void DecodeToWav_NullOutput_Throws()
    {
        using var input = new MemoryStream([1, 2, 3]);
        Assert.Throws<ArgumentNullException>(() => OggDecoder.DecodeToWav(input, null!));
    }

    [Fact]
    public void DecodeToWav_NotAnOggStream_Throws()
    {
        using var input = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        using var output = new MemoryStream();

        Assert.ThrowsAny<Exception>(() => OggDecoder.DecodeToWav(input, output));
    }

    [Fact]
    public void DecodeToWav_EmptyStream_Throws()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        Assert.ThrowsAny<Exception>(() => OggDecoder.DecodeToWav(input, output));
    }
}
