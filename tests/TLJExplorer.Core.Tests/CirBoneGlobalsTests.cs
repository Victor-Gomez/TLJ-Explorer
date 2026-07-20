using System.Numerics;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class CirBoneGlobalsTests
{
    private static CirBone Bone(string name, params int[] children) => new(name, Unknown1: 0, Children: children);

    private static AniKeyframe FirstKeyframe(float posX = 0, float posY = 0, float posZ = 0,
        float qx = 0, float qy = 0, float qz = 0, float qw = 1) =>
        new(AnimTime: 0, QRotX: qx, QRotY: qy, QRotZ: qz, QRotW: qw, PosX: posX, PosY: posY, PosZ: posZ);

    [Fact]
    public void Compute_NullModel_Throws()
    {
        var animation = new AniAnimation { BoneAnims = [] };
        Assert.Throws<ArgumentNullException>(() => CirBoneGlobals.Compute(null!, animation));
    }

    [Fact]
    public void Compute_NullAnimation_Throws()
    {
        var model = new CirModel { Skeleton = [] };
        Assert.Throws<ArgumentNullException>(() => CirBoneGlobals.Compute(model, null!));
    }

    [Fact]
    public void Compute_EmptySkeleton_ReturnsEmptyArray()
    {
        var model = new CirModel { Skeleton = [] };
        var animation = new AniAnimation { BoneAnims = [] };

        Assert.Empty(CirBoneGlobals.Compute(model, animation));
    }

    [Fact]
    public void Compute_RootBoneWithNoAnimTrack_UsesIdentityBindPose()
    {
        var model = new CirModel { Skeleton = [Bone("root")] };
        var animation = new AniAnimation { BoneAnims = [] };

        CirBoneBindPose[] result = CirBoneGlobals.Compute(model, animation);

        Assert.Equal(-1, result[0].Parent);
        Assert.Equal(Quaternion.Identity, result[0].WorldRotation);
        Assert.Equal(Vector3.Zero, result[0].WorldTranslation);
    }

    [Fact]
    public void Compute_RootBoneTranslation_AppliesDirectlyToWorldSpace()
    {
        var model = new CirModel { Skeleton = [Bone("root")] };
        var animation = new AniAnimation
        {
            BoneAnims = [new AniBoneTrack(0, [FirstKeyframe(posX: 10, posY: 20, posZ: 30)])],
        };

        CirBoneBindPose[] result = CirBoneGlobals.Compute(model, animation);

        Assert.Equal(new Vector3(10, 20, 30), result[0].WorldTranslation);
    }

    [Fact]
    public void Compute_ChildBone_ComposesWithParentWorldTransform()
    {
        var model = new CirModel { Skeleton = [Bone("root", 1), Bone("child")] };
        var animation = new AniAnimation
        {
            BoneAnims =
            [
                new AniBoneTrack(0, [FirstKeyframe(posX: 10)]),
                new AniBoneTrack(1, [FirstKeyframe(posX: 1)]),
            ],
        };

        CirBoneBindPose[] result = CirBoneGlobals.Compute(model, animation);

        Assert.Equal(0, result[1].Parent);
        Assert.Equal(new Vector3(11, 0, 0), result[1].WorldTranslation);
    }

    [Fact]
    public void Compute_ChildIndexOutOfRange_IsIgnoredWithoutThrowing()
    {
        var model = new CirModel { Skeleton = [Bone("root", 99)] };
        var animation = new AniAnimation { BoneAnims = [] };

        CirBoneBindPose[] result = CirBoneGlobals.Compute(model, animation);

        Assert.Single(result);
        Assert.Equal(-1, result[0].Parent);
    }

    [Fact]
    public void Compute_BoneIndexOutOfRangeInAnimTrack_IsIgnored()
    {
        var model = new CirModel { Skeleton = [Bone("root")] };
        var animation = new AniAnimation
        {
            BoneAnims = [new AniBoneTrack(BoneIndex: 42, Keyframes: [FirstKeyframe(posX: 999)])],
        };

        CirBoneBindPose[] result = CirBoneGlobals.Compute(model, animation);

        Assert.Equal(Vector3.Zero, result[0].WorldTranslation);
    }

    [Fact]
    public void ToWorld_TransformsBoneLocalPointByWorldRotationAndTranslation()
    {
        var bone = new CirBoneBindPose(
            LocalRotation: Quaternion.Identity, LocalTranslation: Vector3.Zero,
            Parent: -1, Children: [],
            WorldRotation: Quaternion.Identity, WorldTranslation: new Vector3(5, 0, 0));

        Vector3 world = CirBoneGlobals.ToWorld(bone, new Vector3(1, 2, 3));

        Assert.Equal(new Vector3(6, 2, 3), world);
    }

    [Fact]
    public void RotateDirection_IgnoresWorldTranslation()
    {
        var bone = new CirBoneBindPose(
            LocalRotation: Quaternion.Identity, LocalTranslation: Vector3.Zero,
            Parent: -1, Children: [],
            WorldRotation: Quaternion.Identity, WorldTranslation: new Vector3(100, 100, 100));

        Vector3 direction = CirBoneGlobals.RotateDirection(bone, new Vector3(0, 1, 0));

        Assert.Equal(new Vector3(0, 1, 0), direction);
    }
}
