using System.Numerics;
using TLJExplorer.Core.Formats;
using TLJExplorer.Rendering;
using Xunit;

namespace TLJExplorer.Tests;

public class SkeletonPoserTests
{
    [Fact]
    public void IdentityPose_ReturnsOneIdentityMatrixPerBone()
    {
        Matrix4x4[] pose = SkeletonPoser.IdentityPose(3);

        Assert.Equal(3, pose.Length);
        Assert.All(pose, m => Assert.Equal(Matrix4x4.Identity, m));
    }

    [Fact]
    public void IdentityPose_NegativeCount_ReturnsEmpty()
    {
        Assert.Empty(SkeletonPoser.IdentityPose(-1));
    }

    [Fact]
    public void Pose_EmptySkeleton_ReturnsEmptyArray()
    {
        Matrix4x4[] result = SkeletonPoser.Pose([], []);
        Assert.Empty(result);
    }

    [Fact]
    public void Pose_RootWithNoMatchingBonePose_StaysIdentity()
    {
        CirBone[] skeleton = [new CirBone("root", 0, [])];

        Matrix4x4[] result = SkeletonPoser.Pose(skeleton, []);

        Assert.Equal(Matrix4x4.Identity, result[0]);
    }

    [Fact]
    public void Pose_RootTranslation_AppliesDirectly()
    {
        CirBone[] skeleton = [new CirBone("root", 0, [])];
        BonePose[] poses = [new BonePose(0, Quaternion.Identity, new Vector3(1, 2, 3))];

        Matrix4x4[] result = SkeletonPoser.Pose(skeleton, poses);

        Assert.Equal(new Vector3(1, 2, 3), result[0].Translation);
    }

    [Fact]
    public void Pose_ChildInheritsParentWorldTransform()
    {
        // root translated by (10,0,0); child (index 1) translated by (1,0,0) in its own local frame.
        // Child's world position should be the composition: (11,0,0).
        CirBone[] skeleton =
        [
            new CirBone("root", 0, [1]),
            new CirBone("child", 0, []),
        ];
        BonePose[] poses =
        [
            new BonePose(0, Quaternion.Identity, new Vector3(10, 0, 0)),
            new BonePose(1, Quaternion.Identity, new Vector3(1, 0, 0)),
        ];

        Matrix4x4[] result = SkeletonPoser.Pose(skeleton, poses);

        Assert.Equal(new Vector3(10, 0, 0), result[0].Translation);
        Assert.Equal(new Vector3(11, 0, 0), result[1].Translation);
    }

    [Fact]
    public void Pose_OutOfRangeBoneIndexInPose_IsIgnored()
    {
        CirBone[] skeleton = [new CirBone("root", 0, [])];
        BonePose[] poses = [new BonePose(99, Quaternion.Identity, new Vector3(5, 5, 5))];

        Matrix4x4[] result = SkeletonPoser.Pose(skeleton, poses);

        Assert.Equal(Matrix4x4.Identity, result[0]);
    }

    [Fact]
    public void Pose_ChildIndexOutOfRange_IsSkippedWithoutThrowing()
    {
        CirBone[] skeleton = [new CirBone("root", 0, [42])];

        Matrix4x4[] result = SkeletonPoser.Pose(skeleton, []);

        Assert.Single(result);
    }

    [Fact]
    public void Pose_CyclicChildReference_DoesNotInfiniteLoop()
    {
        // Bone 1 lists bone 0 (its own parent) as a child too -- WalkChildren's `visited` guard must
        // stop this from recursing forever.
        CirBone[] skeleton =
        [
            new CirBone("root", 0, [1]),
            new CirBone("child", 0, [0]),
        ];

        Matrix4x4[] result = SkeletonPoser.Pose(skeleton, []);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void Pose_NullSkeleton_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SkeletonPoser.Pose(null!, []));
    }

    [Fact]
    public void Pose_NullBonePoses_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SkeletonPoser.Pose([], null!));
    }
}
