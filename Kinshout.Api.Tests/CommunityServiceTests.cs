using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class CommunityServiceTests
{
    [Fact]
    public async Task CreateAsync_StoresSlugWithoutKPrefix()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = new CommunityService(db);

        var created = await service.CreateAsync(
            user.Id,
            new CreateCommunityRequestDto("k/gombe-news", "Gombe News"));

        Assert.Equal("gombe-news", created.Slug);
        Assert.Equal("k/gombe-news", created.RouteSlug);
        Assert.Equal(0, created.DiscussionCount);

        var stored = Assert.Single(db.Communities);
        Assert.Equal("gombe-news", stored.Slug);
    }

    [Fact]
    public async Task DeleteAsync_SucceedsWhenEmpty()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = new CommunityService(db);
        await service.CreateAsync(user.Id, new CreateCommunityRequestDto("empty-community"));

        await service.DeleteAsync(user.Id, "k/empty-community");

        Assert.Empty(db.Communities);
    }

    [Fact]
    public async Task DeleteAsync_FailsWhenDiscussionsExist()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = new CommunityService(db);
        var community = await service.CreateAsync(user.Id, new CreateCommunityRequestDto("busy"));

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Hello",
            Body = "Body",
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(user.Id, "busy"));

        Assert.Contains("discussions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(db.Communities);
    }

    [Fact]
    public async Task DeleteAsync_FailsForNonCreator()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@test", DisplayName = "Other" };
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var service = new CommunityService(db);
        await service.CreateAsync(user.Id, new CreateCommunityRequestDto("mine"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DeleteAsync(other.Id, "mine"));
    }
}
