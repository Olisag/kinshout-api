using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class CommunitySlugHelperTests
{
    [Theory]
    [InlineData("community1", "community1")]
    [InlineData("k/community1", "community1")]
    [InlineData("K/Community1", "community1")]
    [InlineData("ma-communaute", "ma-communaute")]
    [InlineData("k/ma-communaute", "ma-communaute")]
    public void Normalize_AcceptsValidSlugs(string input, string expected)
    {
        Assert.Equal(expected, CommunitySlugHelper.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("k/")]
    [InlineData("has space")]
    [InlineData("k/has space")]
    [InlineData("Bad_Slug")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    public void Normalize_RejectsInvalidSlugs(string input)
    {
        Assert.Throws<ArgumentException>(() => CommunitySlugHelper.Normalize(input));
    }

    [Fact]
    public void ToRouteSlug_PrefixesWithK()
    {
        Assert.Equal("k/community1", CommunitySlugHelper.ToRouteSlug("community1"));
    }
}
