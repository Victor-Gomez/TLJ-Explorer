using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class ContainerUnwrapTests
{
    [Theory]
    [InlineData(".ovs", ".ogg")]
    [InlineData(".sss", ".smk")]
    [InlineData(".bbb", ".bik")]
    [InlineData("ovs", ".ogg")]
    [InlineData("OVS", ".ogg")]
    [InlineData(".SSS", ".smk")]
    public void GetExtractedExtension_KnownContainers_MapsCorrectly(string sourceExtension, string expected)
    {
        Assert.Equal(expected, ContainerUnwrap.GetExtractedExtension(sourceExtension));
    }

    [Fact]
    public void GetExtractedExtension_UnknownExtension_Throws()
    {
        Assert.Throws<ArgumentException>(() => ContainerUnwrap.GetExtractedExtension(".xyz"));
    }

    [Fact]
    public void GetExtractedExtension_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => ContainerUnwrap.GetExtractedExtension(""));
    }

    [Fact]
    public void ExtractToFile_CopiesBytesVerbatim()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        string path = Path.Combine(Path.GetTempPath(), $"tlj-unwrap-test-{Guid.NewGuid():N}.bin");
        try
        {
            using (var source = new MemoryStream(payload))
                ContainerUnwrap.ExtractToFile(source, path);

            Assert.Equal(payload, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractToFile_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ContainerUnwrap.ExtractToFile(null!, "x.bin"));
    }

    [Fact]
    public void ExtractToFile_EmptyDestinationPath_Throws()
    {
        using var source = new MemoryStream([1]);
        Assert.Throws<ArgumentException>(() => ContainerUnwrap.ExtractToFile(source, ""));
    }
}
