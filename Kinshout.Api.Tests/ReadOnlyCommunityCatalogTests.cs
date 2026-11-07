using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class ReadOnlyCommunityCatalogTests
{
    [Theory]
    [InlineData("musique")]
    [InlineData("classiques")]
    [InlineData("diaspora-fr")]
    public void Member_cannot_post_in_a_read_only_top(string slug)
    {
        var community = new Community { Slug = slug, IsActive = true, CreatedByUserId = Guid.NewGuid() };
        var member = new CommunityMember
        {
            UserId = Guid.NewGuid(),
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
        };

        Assert.False(CommunityAccessHelper.CanPost(community, member, member.UserId));
        Assert.True(CommunityAccessHelper.CanComment(community, member, member.UserId));
    }

    [Fact]
    public void Creator_can_still_post_in_a_read_only_top()
    {
        var creatorId = Guid.NewGuid();
        var community = new Community { Slug = "foi", IsActive = true, CreatedByUserId = creatorId };

        Assert.True(CommunityAccessHelper.CanPost(community, membership: null, creatorId));
    }

    [Fact]
    public void Member_can_post_in_a_writable_room()
    {
        var community = new Community { Slug = "ville", IsActive = true, CreatedByUserId = Guid.NewGuid() };
        var member = new CommunityMember
        {
            UserId = Guid.NewGuid(),
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
        };

        Assert.True(CommunityAccessHelper.CanPost(community, member, member.UserId));
    }

    [Fact]
    public void Trend_score_shrinks_as_a_clip_ages()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var fresh = YouTubeMetricService.TrendScore(1000, 10, now, now);
        var old = YouTubeMetricService.TrendScore(1000, 10, now.AddHours(-72), now);

        Assert.True(fresh > old);
        Assert.Equal(1050, fresh);
    }
}
