using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class CirDeadBabeSentinelTests
{
    [Fact]
    public void Decode_MissingDeadBabeSentinel_ThrowsFormatException()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        w.Write(4);                 // Id
        w.Write(16);                // Version 16 (no Unknown1 pre-magic slot)
        w.Write(0xCAFEBABEu);       // Wrong sentinel — should be 0xDEADBABE
        w.Write(0.0f);              // Unknown3
        w.Write(0);                 // Materials count
        w.Write(0);                 // Unknown4 count
        w.Write(0);                 // Skeleton count
        w.Write(0);                 // Groups count

        stream.Position = 0;

        FormatException ex = Assert.Throws<FormatException>(() => CirDecoder.Decode(stream));
        Assert.Contains("0xDEADBABE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_WithValidSentinel_ParsesEmptyModel()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        w.Write(4);
        w.Write(16);
        w.Write(0xDEADBABEu);
        w.Write(0.0f);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        stream.Position = 0;

        CirModel model = CirDecoder.Decode(stream);
        Assert.Equal(16, model.Version);
        Assert.Empty(model.Materials);
        Assert.Empty(model.Skeleton);
        Assert.Empty(model.Groups);
    }

    [Fact]
    public void Decode_Version256_ReadsExtraUnknown1BeforeSentinel()
    {
        // Version 256 layout: Id (4), Version (256), Unknown1 (Int32), magic (0xDEADBABE), Unknown3 (float), then arrays.
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        w.Write(4);
        w.Write(256);
        w.Write(42);                // Unknown1 (only for version 256)
        w.Write(0xDEADBABEu);
        w.Write(1.5f);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        stream.Position = 0;

        CirModel model = CirDecoder.Decode(stream);
        Assert.Equal(256, model.Version);
    }
}
