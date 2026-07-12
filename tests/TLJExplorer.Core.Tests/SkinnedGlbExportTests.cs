using System.Numerics;
using System.Text;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class SkinnedGlbExportTests
{
    [Fact]
    public void Write_WithBindPoseAni_EmitsSkinJointsAndWeights()
    {
        // Two-bone chain: root at (0,0,0), child under it. Vertex bound to bones 0 and 1 with 50/50 weights.
        CirVertex V(float x, float y) => new(
            PosX1: x, PosY1: y, PosZ1: 0,
            PosX2: x, PosY2: y, PosZ2: 0,
            NormalX: 0, NormalY: 0, NormalZ: 1,
            TextureS: 0, TextureT: 0,
            BoneIndex1: 0, BoneIndex2: 1, BoneWeight: 0.5f);

        var model = new CirModel
        {
            Materials = [new CirMaterial("mat", 0, "tex.tm", 1, 1, 1)],
            Skeleton =
            [
                new CirBone("root",  0, [1]),
                new CirBone("child", 0, []),
            ],
            Groups =
            [
                new CirGroup("g",
                    [new CirFace(0,
                        [V(0, 0), V(1, 0), V(1, 1)],
                        [new CirTriangle(0, 1, 2)])],
                    [], []),
            ],
        };

        // ANI first-keyframe pose: root identity, child translated 2 units on X.
        var ani = new AniAnimation
        {
            Version = 3,
            MaxTime = 1000,
            BoneAnims =
            [
                new AniBoneTrack(0, [new AniKeyframe(0, 0, 0, 0, 1, 0, 0, 0)]),
                new AniBoneTrack(1, [new AniKeyframe(0, 0, 0, 0, 1, 2, 0, 0)]),
            ],
        };

        var stream = new MemoryStream();
        GlbWriter.Write(model, stream, new GlbWriteOptions { BindPose = ani });

        byte[] bytes = stream.ToArray();
        uint jsonLen = BitConverter.ToUInt32(bytes, 12);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen).TrimEnd();

        // Skin + joints must appear.
        Assert.Contains("\"skins\"", json, StringComparison.Ordinal);
        Assert.Contains("\"JOINTS_0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"WEIGHTS_0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inverseBindMatrices\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bone_0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bone_1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithAnimationClips_EmitsAnimationsArrayAndChannels()
    {
        var model = new CirModel
        {
            Skeleton = [new CirBone("root", 0, [])],
            Groups = [],
        };

        var bindPose = new AniAnimation
        {
            Version = 3,
            MaxTime = 1000,
            BoneAnims = [new AniBoneTrack(0, [new AniKeyframe(0, 0, 0, 0, 1, 0, 0, 0)])],
        };

        var clip = new AniAnimation
        {
            Version = 3,
            MaxTime = 500,
            BoneAnims =
            [
                new AniBoneTrack(0,
                [
                    new AniKeyframe(0,   0, 0, 0, 1, 0, 0, 0),
                    new AniKeyframe(500, 0, 1, 0, 0, 1, 0, 0),
                ]),
            ],
        };

        var stream = new MemoryStream();
        GlbWriter.Write(model, stream, new GlbWriteOptions
        {
            BindPose = bindPose,
            Animations = [("walk", clip)],
        });

        byte[] bytes = stream.ToArray();
        uint jsonLen = BitConverter.ToUInt32(bytes, 12);
        string json = Encoding.UTF8.GetString(bytes, 20, (int)jsonLen).TrimEnd();

        Assert.Contains("\"animations\"", json, StringComparison.Ordinal);
        Assert.Contains("\"walk\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rotation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"translation\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CirBoneGlobals_ChildInheritsParentTranslation()
    {
        var model = new CirModel
        {
            Skeleton =
            [
                new CirBone("root", 0, [1]),
                new CirBone("child", 0, []),
            ],
        };
        var ani = new AniAnimation
        {
            Version = 3,
            MaxTime = 1000,
            BoneAnims =
            [
                new AniBoneTrack(0, [new AniKeyframe(0, 0, 0, 0, 1, 5, 0, 0)]),
                new AniBoneTrack(1, [new AniKeyframe(0, 0, 0, 0, 1, 0, 3, 0)]),
            ],
        };

        CirBoneBindPose[] bones = CirBoneGlobals.Compute(model, ani);

        // Root's world = local = (5,0,0). Child's world = root's world · child's local = (5,3,0).
        Assert.Equal(new Vector3(5, 0, 0), bones[0].WorldTranslation);
        Assert.Equal(new Vector3(5, 3, 0), bones[1].WorldTranslation);

        // Sanity: converting the origin from child-bone-local space to world lands at the child's world pos.
        Vector3 origin = CirBoneGlobals.ToWorld(bones[1], Vector3.Zero);
        Assert.Equal(new Vector3(5, 3, 0), origin);
    }
}
