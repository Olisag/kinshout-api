using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionServiceListTests
{
    [Fact]
    public async Task ListAsync_OrdersByPopularThenRecent()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Discussions.AddRange(
            CreateDiscussion(user, category, "Old active", replyCount: 5, createdAt: DateTime.UtcNow.AddDays(-4)),
            CreateDiscussion(user, category, "New active", replyCount: 5, createdAt: DateTime.UtcNow.AddDays(-1)),
            CreateDiscussion(user, category, "Recent quiet", replyCount: 0, createdAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync(sort: ListSortHelper.Popular, pageSize: 10);

        Assert.Equal(3, results.TotalCount);
        Assert.Equal(["New active", "Old active", "Recent quiet"], results.Items.Select(d => d.Title).ToArray());
    }

    [Fact]
    public async Task ListAsync_FiltersAndPaginatesInDatabase()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Discussions.AddRange(
            CreateDiscussion(user, category, "Starlink setup", replyCount: 1, createdAt: DateTime.UtcNow.AddDays(-2)),
            CreateDiscussion(user, category, "Best quartier", replyCount: 2, createdAt: DateTime.UtcNow.AddDays(-1)),
            CreateDiscussion(user, category, "Starlink review", replyCount: 3, createdAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync("starlink", page: 1, pageSize: 1, sort: ListSortHelper.Recent);

        Assert.Equal(2, results.TotalCount);
        Assert.Single(results.Items);
        Assert.Equal("Starlink review", results.Items[0].Title);
        Assert.True(results.HasMore);
    }

    private static DiscussionService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new DiscussionService(db, Mock.Of<IOpenAiService>(), moderation.Object);
    }

    private static Discussion CreateDiscussion(
        User user,
        Category category,
        string title,
        int replyCount,
        DateTime createdAt)
    {
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Body = title,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            User = user,
            Category = category,
        };

        for (var i = 0; i < replyCount; i++)
        {
            discussion.Replies.Add(new DiscussionReply
            {
                UserId = user.Id,
                Body = $"Reply {i}",
                CreatedAt = createdAt.AddMinutes(i),
            });
        }

        return discussion;
    }
}
