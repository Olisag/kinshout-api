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
            CreateDiscussion(user, category, "Old active", viewCount: 5, createdAt: DateTime.UtcNow.AddDays(-4)),
            CreateDiscussion(user, category, "New active", viewCount: 5, createdAt: DateTime.UtcNow.AddDays(-1)),
            CreateDiscussion(user, category, "Recent quiet", viewCount: 0, createdAt: DateTime.UtcNow));
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

    [Fact]
    public async Task ListMineAsync_ReturnsStartedAndRepliedDiscussions()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User
        {
            Email = "other@example.com",
            DisplayName = "Other",
            WhatsAppNumber = "+243900000002",
        };
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var mine = CreateDiscussion(user, category, "My thread", replyCount: 0, createdAt: DateTime.UtcNow.AddDays(-2));
        var replied = CreateDiscussion(other, category, "Other thread", replyCount: 0, createdAt: DateTime.UtcNow.AddDays(-3));
        replied.Replies.Add(new DiscussionReply
        {
            UserId = user.Id,
            Body = "My reply",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });
        var unrelated = CreateDiscussion(other, category, "Unrelated", replyCount: 1, createdAt: DateTime.UtcNow);

        db.Discussions.AddRange(mine, replied, unrelated);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListMineAsync(user.Id);

        Assert.Equal(2, results.TotalCount);
        Assert.Equal(["Other thread", "My thread"], results.Items.Select(d => d.Title).ToArray());
    }

    [Fact]
    public async Task ListMineAsync_FiltersAuthoredAndReplies()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User
        {
            Email = "other@example.com",
            DisplayName = "Other",
            WhatsAppNumber = "+243900000002",
        };
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var mine = CreateDiscussion(user, category, "My thread", replyCount: 0, createdAt: DateTime.UtcNow.AddDays(-2));
        var replied = CreateDiscussion(other, category, "Other thread", replyCount: 0, createdAt: DateTime.UtcNow.AddDays(-3));
        replied.Replies.Add(new DiscussionReply
        {
            UserId = user.Id,
            Body = "My reply",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });
        var ownWithReply = CreateDiscussion(user, category, "My replied thread", replyCount: 0, createdAt: DateTime.UtcNow.AddDays(-4));
        ownWithReply.Replies.Add(new DiscussionReply
        {
            UserId = user.Id,
            Body = "Self reply",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        });

        db.Discussions.AddRange(mine, replied, ownWithReply);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var authored = await service.ListMineAsync(user.Id, filter: DiscussionMineFilterHelper.Authored);
        var replies = await service.ListMineAsync(user.Id, filter: DiscussionMineFilterHelper.Replies);

        Assert.Equal(2, authored.TotalCount);
        Assert.Equal(["My thread", "My replied thread"], authored.Items.Select(d => d.Title).ToArray());
        Assert.Single(replies.Items);
        Assert.Equal("Other thread", replies.Items[0].Title);
    }

    [Fact]
    public async Task ListMineAsync_PaginatesResults()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        for (var i = 0; i < 5; i++)
            db.Discussions.Add(CreateDiscussion(user, category, $"Thread {i}", replyCount: 0, createdAt: DateTime.UtcNow.AddDays(-i)));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var page1 = await service.ListMineAsync(user.Id, page: 1, pageSize: 2);

        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page1.TotalCount);
        Assert.True(page1.HasMore);
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
        int replyCount = 0,
        int viewCount = 0,
        DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.UtcNow;
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = title,
            Body = title,
            CreatedAt = at,
            UpdatedAt = at,
            ReplyCount = replyCount,
            ViewCount = viewCount,
            User = user,
            Category = category,
        };

        for (var i = 0; i < replyCount; i++)
        {
            discussion.Replies.Add(new DiscussionReply
            {
                UserId = user.Id,
                Body = $"Reply {i}",
                CreatedAt = at.AddMinutes(i),
            });
        }

        return discussion;
    }
}
