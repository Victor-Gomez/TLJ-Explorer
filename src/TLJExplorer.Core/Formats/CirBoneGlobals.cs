using System.Numerics;

namespace TLJExplorer.Core.Formats;

/// <summary>Per-bone local + global bind-pose data derived from a CIR skeleton and an ANI first keyframe.</summary>
/// <param name="LocalRotation">Bone's local-space rotation (relative to its parent).</param>
/// <param name="LocalTranslation">Bone's local-space translation (relative to its parent).</param>
/// <param name="Parent">Parent bone index, or <c>-1</c> for a root bone.</param>
/// <param name="Children">Direct children of this bone.</param>
/// <param name="WorldRotation">Bone's world-space rotation (composed down the hierarchy).</param>
/// <param name="WorldTranslation">Bone's world-space translation.</param>
public readonly record struct CirBoneBindPose(
    Quaternion LocalRotation,
    Vector3 LocalTranslation,
    int Parent,
    int[] Children,
    Quaternion WorldRotation,
    Vector3 WorldTranslation);

/// <summary>
/// Computes the bind pose of a CIR skeleton from the first sample of an ANI animation.
/// </summary>
/// <remarks>
/// A CIR file stores no bone transforms of its own — the bind pose comes entirely from an ANI's first
/// keyframe. Without one, vertex positions are unintelligible bone-local fragments (see
/// <c>cir_obj.py</c>'s warning: "the exported mesh will look like a jumble of bone-local fragments").
/// This helper mirrors the Python script's <c>compute_bone_globals</c>: it samples the ANI at t=0, fills
/// in identity for bones the ANI doesn't touch, and composes hierarchically to world space.
/// </remarks>
public static class CirBoneGlobals
{
    /// <summary>Builds a per-bone bind pose table for <paramref name="model"/> from <paramref name="animation"/>'s first keyframe.</summary>
    public static CirBoneBindPose[] Compute(CirModel model, AniAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(animation);

        int n = model.Skeleton.Length;

        BonePose[] sampled = AnimationSampler.Sample(animation, 0f, smoothWrap: false, out _);
        var local = new (Quaternion Rot, Vector3 Pos)[n];
        for (int i = 0; i < n; i++)
            local[i] = (Quaternion.Identity, Vector3.Zero);
        foreach (BonePose bp in sampled)
        {
            if (bp.BoneIndex >= 0 && bp.BoneIndex < n)
                local[bp.BoneIndex] = (Normalize(bp.Rotation), bp.Position);
        }

        int[] parent = new int[n];
        Array.Fill(parent, -1);
        for (int i = 0; i < n; i++)
        {
            foreach (int c in model.Skeleton[i].Children)
            {
                if (c >= 0 && c < n)
                    parent[c] = i;
            }
        }

        var result = new CirBoneBindPose[n];
        var resolved = new bool[n];

        for (int i = 0; i < n; i++)
            Resolve(i);

        return result;

        void Resolve(int i)
        {
            if (resolved[i])
                return;

            (Quaternion lrot, Vector3 lpos) = local[i];
            Quaternion wrot;
            Vector3 wpos;
            if (parent[i] < 0)
            {
                wrot = lrot;
                wpos = lpos;
            }
            else
            {
                Resolve(parent[i]);
                CirBoneBindPose p = result[parent[i]];
                // Compose (parent rot ∘ local rot) and (parent pos + parent rot · local pos). Same math
                // as the Python `_q_mul` / `_q_rotate` helpers in cir_obj.py.
                wrot = Normalize(Multiply(p.WorldRotation, lrot));
                wpos = p.WorldTranslation + RotateVector(p.WorldRotation, lpos);
            }

            result[i] = new CirBoneBindPose(
                LocalRotation: lrot,
                LocalTranslation: lpos,
                Parent: parent[i],
                Children: (int[])model.Skeleton[i].Children.Clone(),
                WorldRotation: wrot,
                WorldTranslation: wpos);

            resolved[i] = true;
        }
    }

    /// <summary>Transforms a bone-local point to world space via <paramref name="bone"/>'s global rotation and translation.</summary>
    public static Vector3 ToWorld(CirBoneBindPose bone, Vector3 boneLocal) =>
        RotateVector(bone.WorldRotation, boneLocal) + bone.WorldTranslation;

    /// <summary>Rotates a direction vector (no translation) by <paramref name="bone"/>'s world rotation.</summary>
    public static Vector3 RotateDirection(CirBoneBindPose bone, Vector3 direction) =>
        RotateVector(bone.WorldRotation, direction);

    /// <summary>Standard Hamilton product used for composing bone rotations up the hierarchy.</summary>
    private static Quaternion Multiply(Quaternion a, Quaternion b)
    {
        // System.Numerics' `a * b` operator matches this convention (Transform(v, a*b) = Transform(Transform(v, b), a)),
        // but we spell it out here to make the composition ordering unambiguous in code review.
        return new Quaternion(
            (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
            (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
            (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
            (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));
    }

    /// <summary>Rotates <paramref name="v"/> by unit quaternion <paramref name="q"/>.</summary>
    private static Vector3 RotateVector(Quaternion q, Vector3 v)
    {
        // v' = v + 2 * q.xyz × (q.xyz × v + q.w * v); the standard optimised formula avoids computing q^-1.
        var qv = new Vector3(q.X, q.Y, q.Z);
        Vector3 t = 2f * Vector3.Cross(qv, v);
        return v + (q.W * t) + Vector3.Cross(qv, t);
    }

    private static Quaternion Normalize(Quaternion q)
    {
        float len = MathF.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
        return len > 1e-8f
            ? new Quaternion(q.X / len, q.Y / len, q.Z / len, q.W / len)
            : Quaternion.Identity;
    }
}
