namespace TLJExplorer.Core.Formats;

/// <summary>A single keyframe within an <see cref="AniBoneTrack"/>.</summary>
/// <param name="AnimTime">Time (milliseconds) at which this keyframe applies.</param>
/// <param name="QRotX">Rotation quaternion X component.</param>
/// <param name="QRotY">Rotation quaternion Y component.</param>
/// <param name="QRotZ">Rotation quaternion Z component.</param>
/// <param name="QRotW">Rotation quaternion W component.</param>
/// <param name="PosX">Bone position X at this keyframe.</param>
/// <param name="PosY">Bone position Y at this keyframe.</param>
/// <param name="PosZ">Bone position Z at this keyframe.</param>
public sealed record AniKeyframe(int AnimTime, float QRotX, float QRotY, float QRotZ, float QRotW, float PosX, float PosY, float PosZ);

/// <summary>The full animation track (keyframes over time) for a single bone.</summary>
/// <param name="BoneIndex">Index of the animated bone within the corresponding <see cref="CirModel.Skeleton"/>.</param>
/// <param name="Keyframes">Keyframes for this bone, expected to be ordered by <see cref="AniKeyframe.AnimTime"/>.</param>
public sealed record AniBoneTrack(int BoneIndex, AniKeyframe[] Keyframes);

/// <summary>
/// A fully decoded ANI skeletal animation: a set of per-bone keyframe tracks. Produced by
/// <see cref="AniDecoder"/> from the reverse-engineered binary ANI file format.
/// </summary>
public sealed class AniAnimation
{
    /// <summary>File format version, either 3 or 256.</summary>
    public int Version { get; init; }

    /// <summary>Total duration of the animation, in milliseconds.</summary>
    public int MaxTime { get; init; }

    /// <summary>Per-bone keyframe tracks.</summary>
    public AniBoneTrack[] BoneAnims { get; init; } = [];
}
