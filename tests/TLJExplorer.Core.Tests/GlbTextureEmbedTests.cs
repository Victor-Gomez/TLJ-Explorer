using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class GlbTextureEmbedTests
{
    [Fact]
    public void Write_WithTextureResolver_EmbedsImagesTexturesSamplersAndPointsMaterial()
    {
        var model = new CirModel
        {
            Materials = [new CirMaterial("m", 0, "brick.tm", 1, 1, 1)],
            Skeleton = [new CirBone("root", 0, [])],
            Groups =
            [
                new CirGroup("g",
                    [new CirFace(0,
                        [Corner(0, 0), Corner(1, 0), Corner(1, 1)],
                        [new CirTriangle(0, 1, 2)])],
                    [], []),
            ],
        };

        byte[] pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03];

        var stream = new MemoryStream();
        GlbWriter.Write(model, stream, new GlbWriteOptions
        {
            TextureResolver = _ => pngMagic,
        });

        byte[] bytes = stream.ToArray();
        uint jsonLen = BitConverter.ToUInt32(bytes, 12);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen).TrimEnd();

        Assert.Contains("\"images\"", json, StringComparison.Ordinal);
        Assert.Contains("\"image/png\"", json, StringComparison.Ordinal);
        Assert.Contains("\"textures\"", json, StringComparison.Ordinal);
        Assert.Contains("\"samplers\"", json, StringComparison.Ordinal);
        Assert.Contains("\"baseColorTexture\":{\"index\":0}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_TextureResolverReturnsNull_LeavesMaterialFlatColoured()
    {
        var model = new CirModel
        {
            Materials = [new CirMaterial("m", 0, "unresolvable.tm", 0.4f, 0.5f, 0.6f)],
            Skeleton = [new CirBone("root", 0, [])],
            Groups = [],
        };

        var stream = new MemoryStream();
        GlbWriter.Write(model, stream, new GlbWriteOptions { TextureResolver = _ => null });

        byte[] bytes = stream.ToArray();
        uint jsonLen = BitConverter.ToUInt32(bytes, 12);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen).TrimEnd();

        Assert.DoesNotContain("\"images\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"baseColorTexture\"", json, StringComparison.Ordinal);
        Assert.Contains("\"baseColorFactor\":[0.4,0.5,0.6,1]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_DuplicateTextureName_SharesOneImage()
    {
        var model = new CirModel
        {
            Materials =
            [
                new CirMaterial("a", 0, "shared.tm", 1, 1, 1),
                new CirMaterial("b", 0, "shared.tm", 1, 1, 1),
            ],
            Skeleton = [new CirBone("root", 0, [])],
            Groups = [],
        };

        int resolverCalls = 0;
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 1, 2, 3];

        var stream = new MemoryStream();
        GlbWriter.Write(model, stream, new GlbWriteOptions
        {
            TextureResolver = _ => { resolverCalls++; return png; },
        });

        // Both materials share the "shared.tm" texture; the resolver should be called exactly once.
        Assert.Equal(1, resolverCalls);

        byte[] bytes = stream.ToArray();
        uint jsonLen = BitConverter.ToUInt32(bytes, 12);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen).TrimEnd();

        // Both materials point at texture index 0.
        int firstOccurrence = json.IndexOf("\"baseColorTexture\":{\"index\":0}", StringComparison.Ordinal);
        int secondOccurrence = json.IndexOf("\"baseColorTexture\":{\"index\":0}", firstOccurrence + 1, StringComparison.Ordinal);
        Assert.NotEqual(-1, firstOccurrence);
        Assert.NotEqual(-1, secondOccurrence);
    }

    private static CirVertex Corner(float x, float y) => new(
        PosX1: x, PosY1: y, PosZ1: 0,
        PosX2: x, PosY2: y, PosZ2: 0,
        NormalX: 0, NormalY: 0, NormalZ: 1,
        TextureS: 0, TextureT: 0,
        BoneIndex1: 0, BoneIndex2: 0, BoneWeight: 1f);
}
