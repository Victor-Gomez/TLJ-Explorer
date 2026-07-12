using System.Numerics;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Rendering;

/// <summary>
/// Computes per-bone world-space transform matrices for a <see cref="CirModel"/>'s skeleton from a set of
/// sampled local <see cref="BonePose"/>s. CIR stores no bind-pose transforms itself -- the only source of
/// per-bone spatial data is <c>.ani</c> keyframes sampled by <see cref="AnimationSampler"/>.
/// </summary>
public static class SkeletonPoser
{
    /// <summary>Returns <paramref name="boneCount"/> identity matrices as a "no animation" fallback pose.</summary>
    public static Matrix4x4[] IdentityPose(int boneCount)
    {
        var result = new Matrix4x4[Math.Max(0, boneCount)];
        Array.Fill(result, Matrix4x4.Identity);
        return result;
    }

    /// <summary>
    /// Computes a world-space transform matrix for every bone in <paramref name="skeleton"/>. Bones without
    /// a matching entry in <paramref name="bonePoses"/> default to an identity local transform.
    /// </summary>
    public static Matrix4x4[] Pose(CirBone[] skeleton, BonePose[] bonePoses)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(bonePoses);

        var localByBone = new Matrix4x4[skeleton.Length];
        Array.Fill(localByBone, Matrix4x4.Identity);

        foreach (BonePose pose in bonePoses)
        {
            if (pose.BoneIndex < 0 || pose.BoneIndex >= skeleton.Length)
                continue;

            localByBone[pose.BoneIndex] =
                Matrix4x4.CreateFromQuaternion(pose.Rotation) * Matrix4x4.CreateTranslation(pose.Position);
        }

        var worldByBone = new Matrix4x4[skeleton.Length];
        Array.Fill(worldByBone, Matrix4x4.Identity);

        if (skeleton.Length == 0)
            return worldByBone;

        var visited = new bool[skeleton.Length];
        worldByBone[0] = localByBone[0];
        visited[0] = true;

        WalkChildren(skeleton, 0, localByBone, worldByBone, visited);

        return worldByBone;
    }

    private static void WalkChildren(CirBone[] skeleton, int boneIndex, Matrix4x4[] localByBone, Matrix4x4[] worldByBone, bool[] visited)
    {
        foreach (int childIndex in skeleton[boneIndex].Children)
        {
            if (childIndex < 0 || childIndex >= skeleton.Length || visited[childIndex])
                continue;

            visited[childIndex] = true;

            // System.Numerics uses row-vector convention (p' = p * M), so composing "child local, then
            // parent world" requires local on the LEFT. Swapping this to (parentWorld * local) silently
            // produces a plausible-but-wrong pose.
            worldByBone[childIndex] = localByBone[childIndex] * worldByBone[boneIndex];

            WalkChildren(skeleton, childIndex, localByBone, worldByBone, visited);
        }
    }
}
