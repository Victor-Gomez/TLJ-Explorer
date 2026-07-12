using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class GlbWriterTests
{
    [Fact]
    public void Write_EmptyModel_ProducesValidGlbHeaderAndChunks()
    {
        var model = new CirModel();
        var stream = new MemoryStream();

        GlbWriter.Write(model, stream);

        byte[] bytes = stream.ToArray();
        Assert.True(bytes.Length > 20);

        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0)); // "glTF"
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));

        uint jsonChunkLen = BitConverter.ToUInt32(bytes, 12);
        uint jsonChunkType = BitConverter.ToUInt32(bytes, 16);
        Assert.Equal(0x4E4F534Au /* "JSON" */, jsonChunkType);

        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonChunkLen).TrimEnd();
        Assert.StartsWith("{", json);
        Assert.Contains("\"asset\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\":\"2.0\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_SingleTriangleModel_EmitsOnePrimitiveWithMaterial()
    {
        // Build a CirModel with a single triangle in a single face and a single material.
        CirVertex Corner(float x, float y) => new(
            PosX1: x, PosY1: y, PosZ1: 0,
            PosX2: x, PosY2: y, PosZ2: 0,
            NormalX: 0, NormalY: 0, NormalZ: 1,
            TextureS: 0, TextureT: 0,
            BoneIndex1: 0, BoneIndex2: 0, BoneWeight: 1f);

        var model = new CirModel
        {
            Materials = [new CirMaterial("brick", 0, "brick.tm", 1f, 0.5f, 0.25f)],
            Skeleton = [new CirBone("root", 0, [])],
            Groups =
            [
                new CirGroup(
                    "g",
                    [new CirFace(
                        0,
                        [Corner(0, 0), Corner(1, 0), Corner(1, 1)],
                        [new CirTriangle(0, 1, 2)])],
                    [], [])
            ],
        };

        var stream = new MemoryStream();
        GlbWriter.Write(model, stream);

        byte[] bytes = stream.ToArray();
        uint jsonLen = BitConverter.ToUInt32(bytes, 12);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen).TrimEnd();

        Assert.Contains("\"POSITION\"", json, StringComparison.Ordinal);
        Assert.Contains("\"NORMAL\"", json, StringComparison.Ordinal);
        Assert.Contains("\"TEXCOORD_0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"indices\"", json, StringComparison.Ordinal);
        Assert.Contains("\"material\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"brick\"", json, StringComparison.Ordinal);
    }
}
