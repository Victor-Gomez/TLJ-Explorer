using TLJExplorer.Services;
using Xunit;

namespace TLJExplorer.Tests;

public class ResourceTypeFilterTests
{
    [Fact]
    public void All_MatchesEveryFileName()
    {
        Assert.True(ResourceTypeFilter.All.Matches("anything.xmg"));
        Assert.True(ResourceTypeFilter.All.Matches("no-extension"));
    }

    [Theory]
    [InlineData("face.xmg", true)]
    [InlineData("face.XMG", true)]
    [InlineData("skin.tm", true)]
    [InlineData("clip.ovs", false)]
    public void ImagesCategory_MatchesOnlyItsExtensions(string fileName, bool expected)
    {
        ResourceTypeFilter images = ResourceTypeFilter.Categories.Single(c => c.Label.StartsWith("Images"));
        Assert.Equal(expected, images.Matches(fileName));
    }

    [Theory]
    [InlineData("line.ovs")]
    [InlineData("line.isn")]
    [InlineData("line.iss")]
    [InlineData("line.ssn")]
    [InlineData("line.sn")]
    public void SoundsCategory_MatchesAllSoundExtensions(string fileName)
    {
        ResourceTypeFilter sounds = ResourceTypeFilter.Categories.Single(c => c.Label.StartsWith("Sounds"));
        Assert.True(sounds.Matches(fileName));
    }

    [Fact]
    public void Categories_AreAllDistinctAndIncludeAll()
    {
        Assert.Contains(ResourceTypeFilter.All, ResourceTypeFilter.Categories);
        Assert.Equal(ResourceTypeFilter.Categories.Count, ResourceTypeFilter.Categories.Select(c => c.Label).Distinct().Count());
    }

    [Fact]
    public void ToString_ReturnsLabel()
    {
        Assert.Equal("All Types", ResourceTypeFilter.All.ToString());
    }
}
