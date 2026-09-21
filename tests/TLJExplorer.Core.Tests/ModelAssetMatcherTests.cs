using TLJExplorer.Core.FileSystem;
using TLJExplorer.Core.Formats;
using Xunit;

namespace TLJExplorer.Core.Tests;

public class ModelAssetMatcherTests
{
    [Fact]
    public void FindMatchingModel_EmmaTalkAni_MatchesEmmaCirOverCharlesCir()
    {
        var aniNode = new FsNode { Name = "emma_talk.ani" };
        var charles = new FsNode { Name = "charles.cir" };
        var emma = new FsNode { Name = "emma.cir" };

        var matched = ModelAssetMatcher.FindMatchingModel(aniNode, [charles, emma]);

        Assert.Same(emma, matched);
    }

    [Fact]
    public void FindMatchingModel_CharlesWalkAni_MatchesCharlesCirOverEmmaCir()
    {
        var aniNode = new FsNode { Name = "charles_walk.ani" };
        var charles = new FsNode { Name = "charles.cir" };
        var emma = new FsNode { Name = "emma.cir" };

        var matched = ModelAssetMatcher.FindMatchingModel(aniNode, [emma, charles]);

        Assert.Same(charles, matched);
    }

    [Fact]
    public void FindMatchingModel_ExactStemMatch_Wins()
    {
        var aniNode = new FsNode { Name = "crow.ani" };
        var crow = new FsNode { Name = "crow.cir" };
        var crowAlt = new FsNode { Name = "crow_alt.cir" };

        var matched = ModelAssetMatcher.FindMatchingModel(aniNode, [crowAlt, crow]);

        Assert.Same(crow, matched);
    }

    [Fact]
    public void FindMatchingModel_NoMatch_FallsBackToFirstWhenAllowed()
    {
        var aniNode = new FsNode { Name = "generic_action.ani" };
        var first = new FsNode { Name = "charles.cir" };
        var second = new FsNode { Name = "emma.cir" };

        var matched = ModelAssetMatcher.FindMatchingModel(aniNode, [first, second], fallbackToFirst: true);

        Assert.Same(first, matched);
    }

    [Fact]
    public void FindMatchingModel_NoMatch_ReturnsNullWhenFallbackDisabled()
    {
        var aniNode = new FsNode { Name = "generic_action.ani" };
        var first = new FsNode { Name = "charles.cir" };
        var second = new FsNode { Name = "emma.cir" };

        var matched = ModelAssetMatcher.FindMatchingModel(aniNode, [first, second], fallbackToFirst: false);

        Assert.Null(matched);
    }

    [Fact]
    public void FindMatchingAnimation_EmmaCir_MatchesEmmaOverCharles()
    {
        var emma = new FsNode { Name = "emma.cir" };
        var charlesWalk = new FsNode { Name = "charles_walk.ani" };
        var emmaTalk = new FsNode { Name = "emma_talk.ani" };

        var matched = ModelAssetMatcher.FindMatchingAnimation(emma, [charlesWalk, emmaTalk]);

        Assert.Same(emmaTalk, matched);
    }

    [Fact]
    public void FindMatchingAnimation_PrefersIdleOverTalk()
    {
        var emma = new FsNode { Name = "emma.cir" };
        var emmaTalk = new FsNode { Name = "emma_talk.ani" };
        var emmaIdle = new FsNode { Name = "emma_idle.ani" };

        var matched = ModelAssetMatcher.FindMatchingAnimation(emma, [emmaTalk, emmaIdle]);

        Assert.Same(emmaIdle, matched);
    }

    [Fact]
    public void FindMatchingSkin_EmmaCir_MatchesEmmaTmOverCharlesTm()
    {
        var emma = new FsNode { Name = "emma.cir" };
        var charlesTm = new FsNode { Name = "charles.tm" };
        var emmaTm = new FsNode { Name = "emma.tm" };

        var matched = ModelAssetMatcher.FindMatchingSkin(emma, null, [charlesTm, emmaTm]);

        Assert.Same(emmaTm, matched);
    }

    [Fact]
    public void FindMatchingSkin_MaterialRequestedTexture_WinsOverModelStem()
    {
        var modelNode = new FsNode { Name = "guard.cir" };
        var mat = new CirMaterial("mat", 0, "armor_heavy.tm", 1f, 1f, 1f);
        var model = new CirModel { Materials = [mat] };

        var guardTm = new FsNode { Name = "guard.tm" };
        var armorTm = new FsNode { Name = "armor_heavy.tm" };

        var matched = ModelAssetMatcher.FindMatchingSkin(modelNode, model, [guardTm, armorTm]);

        Assert.Same(armorTm, matched);
    }

    [Fact]
    public void FindMatchingSkin_NoMatch_FallsBackToFirst()
    {
        var modelNode = new FsNode { Name = "unknown.cir" };
        var firstSkin = new FsNode { Name = "charles.tm" };
        var secondSkin = new FsNode { Name = "emma.tm" };

        var matched = ModelAssetMatcher.FindMatchingSkin(modelNode, null, [firstSkin, secondSkin], fallbackToFirst: true);

        Assert.Same(firstSkin, matched);
    }
}
