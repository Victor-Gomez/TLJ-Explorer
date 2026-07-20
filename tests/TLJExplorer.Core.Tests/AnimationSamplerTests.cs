using System.Numerics;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class AnimationSamplerTests
{
    private static AniAnimation MakeAnimation(int maxTime, params AniKeyframe[] keyframes) =>
        new()
        {
            Version = 3,
            MaxTime = maxTime,
            BoneAnims = [new AniBoneTrack(BoneIndex: 0, Keyframes: keyframes)],
        };

    private static AniKeyframe Identity(int time, float posX = 0, float posY = 0, float posZ = 0) =>
        new(time, 0, 0, 0, 1, posX, posY, posZ);

    [Fact]
    public void Sample_NullAnimation_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AnimationSampler.Sample(null!, 0f, false, out _));
    }

    [Fact]
    public void Sample_EmptyKeyframeTrack_ReturnsIdentityPose()
    {
        var animation = MakeAnimation(1000, []);

        BonePose[] poses = AnimationSampler.Sample(animation, 500f, false, out _);

        Assert.Single(poses);
        Assert.Equal(Quaternion.Identity, poses[0].Rotation);
        Assert.Equal(Vector3.Zero, poses[0].Position);
    }

    [Fact]
    public void Sample_SingleKeyframeTrack_AlwaysReturnsThatKeyframe()
    {
        var animation = MakeAnimation(1000, Identity(0, posX: 5));

        BonePose[] poses = AnimationSampler.Sample(animation, 999f, false, out _);

        Assert.Equal(new Vector3(5, 0, 0), poses[0].Position);
    }

    [Fact]
    public void Sample_MidpointBetweenTwoKeyframes_LerpsPosition()
    {
        var animation = MakeAnimation(1000, Identity(0, posX: 0), Identity(1000, posX: 10));

        BonePose[] poses = AnimationSampler.Sample(animation, 500f, false, out float progress);

        Assert.Equal(5f, poses[0].Position.X, precision: 3);
        Assert.Equal(0.5f, progress, precision: 3);
    }

    [Fact]
    public void Sample_ExactKeyframeTime_ReturnsThatKeyframeExactly()
    {
        // Use a middle keyframe rather than one at MaxTime: wrapping (t % MaxTime) would otherwise
        // fold a sample taken exactly at MaxTime back down to 0.
        var animation = MakeAnimation(1000, Identity(0, posX: 0), Identity(500, posX: 4), Identity(1000, posX: 10));

        BonePose[] poses = AnimationSampler.Sample(animation, 500f, false, out float progress);

        Assert.Equal(4f, poses[0].Position.X, precision: 3);
        Assert.Equal(0.5f, progress, precision: 3);
    }

    [Fact]
    public void Sample_TimeBeyondMaxTimeWithoutSmoothWrap_WrapsToStart()
    {
        var animation = MakeAnimation(1000, Identity(0, posX: 0), Identity(1000, posX: 10));

        // 1200ms wraps to 200ms (1200 % 1000), 20% of the way from 0 -> 10.
        BonePose[] poses = AnimationSampler.Sample(animation, 1200f, false, out _);

        Assert.Equal(2f, poses[0].Position.X, precision: 3);
    }

    [Fact]
    public void Sample_NegativeTime_WrapsPositive()
    {
        var animation = MakeAnimation(1000, Identity(0, posX: 0), Identity(1000, posX: 10));

        BonePose[] poses = AnimationSampler.Sample(animation, -200f, false, out _);

        // -200 wraps to 800 -> 80% of the way from 0 to 10.
        Assert.Equal(8f, poses[0].Position.X, precision: 3);
    }

    [Fact]
    public void Sample_ZeroMaxTime_ReturnsZeroProgressAndFirstKeyframe()
    {
        var animation = MakeAnimation(0, Identity(0, posX: 7));

        BonePose[] poses = AnimationSampler.Sample(animation, 0f, false, out float progress);

        Assert.Equal(0f, progress);
        Assert.Equal(7f, poses[0].Position.X, precision: 3);
    }

    [Fact]
    public void Sample_SmoothWrapWindow_InterpolatesFromLastKeyframeBackToFirst()
    {
        var animation = MakeAnimation(1000, Identity(0, posX: 0), Identity(1000, posX: 10));

        // Smooth-wrap window is (1000, 1000 + 1000/30]. Halfway through it, the pose should be
        // roughly halfway back from the last keyframe's position (10) to the first's (0).
        float wrapWindowMidpoint = 1000f + (1000f / 60f);
        BonePose[] poses = AnimationSampler.Sample(animation, wrapWindowMidpoint, true, out _);

        Assert.Equal(5f, poses[0].Position.X, precision: 1);
    }

    [Fact]
    public void Sample_MultipleBoneTracks_ProducesOnePosePerTrack()
    {
        var animation = new AniAnimation
        {
            MaxTime = 100,
            BoneAnims =
            [
                new AniBoneTrack(0, [Identity(0)]),
                new AniBoneTrack(1, [Identity(0)]),
                new AniBoneTrack(2, [Identity(0)]),
            ],
        };

        BonePose[] poses = AnimationSampler.Sample(animation, 0f, false, out _);

        Assert.Equal(3, poses.Length);
        Assert.Equal([0, 1, 2], poses.Select(p => p.BoneIndex));
    }
}
