using System.Numerics;

namespace TLJExplorer.Core.Formats;

/// <summary>The interpolated pose of a single bone at a sampled point in time.</summary>
/// <param name="BoneIndex">Index of the bone within the corresponding <see cref="CirModel.Skeleton"/>.</param>
/// <param name="Rotation">Interpolated (SLERP) rotation quaternion.</param>
/// <param name="Position">Interpolated (linear) position.</param>
public readonly record struct BonePose(int BoneIndex, Quaternion Rotation, Vector3 Position);

/// <summary>
/// Stateless helper that samples an <see cref="AniAnimation"/> at an arbitrary point in time, producing
/// an interpolated pose per animated bone.
/// </summary>
/// <remarks>
/// Pure data-in/data-out: this type only computes bone poses. Actual GPU skinning happens in the
/// Silk.NET-based rendering layer.
/// </remarks>
public static class AnimationSampler
{
    /// <summary>Extra time (ms) appended to the animation's natural length when <c>smoothWrap</c> is used to loop back to the start.</summary>
    private const float WrapTimeMs = 1000f / 30f;

    /// <summary>
    /// Computes the interpolated pose of every animated bone at <paramref name="currentTimeMs"/>.
    /// </summary>
    /// <param name="animation">The animation to sample.</param>
    /// <param name="currentTimeMs">The time to sample at, in milliseconds. Wrapped modulo the
    /// animation's duration (see <paramref name="smoothWrap"/> for the wrapping behaviour).</param>
    /// <param name="smoothWrap">
    /// When <see langword="true"/>, <paramref name="currentTimeMs"/> is allowed to run up to
    /// <c>animation.MaxTime + WrapTimeMs</c> (rather than wrapping strictly at <c>MaxTime</c>); within
    /// that extra window, each bone track is interpolated from its LAST keyframe back to its FIRST
    /// keyframe, smoothing the seam where a looping animation repeats.
    /// </param>
    /// <param name="progressFraction">Receives the playback position as a 0..1 fraction, suitable for
    /// driving a UI progress bar.</param>
    /// <returns>One <see cref="BonePose"/> per bone track in <see cref="AniAnimation.BoneAnims"/>.</returns>
    public static BonePose[] Sample(AniAnimation animation, float currentTimeMs, bool smoothWrap, out float progressFraction)
    {
        ArgumentNullException.ThrowIfNull(animation);

        float maxTime = animation.MaxTime;
        float wrapLimit = smoothWrap ? maxTime + WrapTimeMs : maxTime;

        float t = wrapLimit > 0f
            ? Wrap(currentTimeMs, wrapLimit)
            : 0f;

        progressFraction = maxTime > 0f ? Math.Clamp(t / maxTime, 0f, 1f) : 0f;

        var poses = new BonePose[animation.BoneAnims.Length];
        for (int i = 0; i < animation.BoneAnims.Length; i++)
        {
            var track = animation.BoneAnims[i];
            poses[i] = SampleTrack(track, t, maxTime, smoothWrap);
        }

        return poses;
    }

    private static float Wrap(float value, float limit)
    {
        float wrapped = value % limit;
        return wrapped < 0f ? wrapped + limit : wrapped;
    }

    private static BonePose SampleTrack(AniBoneTrack track, float t, float maxTime, bool smoothWrap)
    {
        var keyframes = track.Keyframes;
        if (keyframes.Length == 0)
        {
            return new BonePose(track.BoneIndex, Quaternion.Identity, Vector3.Zero);
        }

        if (keyframes.Length == 1)
        {
            var only = keyframes[0];
            return new BonePose(track.BoneIndex, ToQuaternion(only), ToPosition(only));
        }

        // In the smooth-wrap extension window (t beyond maxTime), interpolate from the last keyframe
        // back to the first, to smooth the animation's loop seam.
        if (smoothWrap && t > maxTime)
        {
            var last = keyframes[^1];
            var first = keyframes[0];
            float span = (maxTime + WrapTimeMs) - last.AnimTime;
            float delta = span > 0f ? Math.Clamp((t - last.AnimTime) / span, 0f, 1f) : 0f;
            return Interpolate(track.BoneIndex, last, first, delta);
        }

        // Find the bracketing keyframes: the last keyframe with AnimTime <= t, and the first keyframe
        // with AnimTime >= t.
        AniKeyframe? lower = null;
        AniKeyframe? upper = null;

        for (int i = 0; i < keyframes.Length; i++)
        {
            var kf = keyframes[i];
            if (kf.AnimTime <= t)
            {
                lower = kf;
            }

            if (kf.AnimTime >= t && upper is null)
            {
                upper = kf;
            }
        }

        lower ??= keyframes[0];
        upper ??= keyframes[^1];

        if (lower.AnimTime == upper.AnimTime)
        {
            return new BonePose(track.BoneIndex, ToQuaternion(lower), ToPosition(lower));
        }

        float d = (t - lower.AnimTime) / (upper.AnimTime - lower.AnimTime);
        return Interpolate(track.BoneIndex, lower, upper, Math.Clamp(d, 0f, 1f));
    }

    private static BonePose Interpolate(int boneIndex, AniKeyframe from, AniKeyframe to, float delta)
    {
        var rotation = Quaternion.Slerp(ToQuaternion(from), ToQuaternion(to), delta);
        var position = Vector3.Lerp(ToPosition(from), ToPosition(to), delta);
        return new BonePose(boneIndex, rotation, position);
    }

    private static Quaternion ToQuaternion(AniKeyframe kf) => new(kf.QRotX, kf.QRotY, kf.QRotZ, kf.QRotW);

    private static Vector3 ToPosition(AniKeyframe kf) => new(kf.PosX, kf.PosY, kf.PosZ);
}
