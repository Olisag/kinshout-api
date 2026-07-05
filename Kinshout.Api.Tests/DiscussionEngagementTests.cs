using System.Text.Json;
using System.Text.Json.Serialization;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class LikedDiscussionServiceTests
{
    [Fact]
    public async Task LikeAsync_IncrementsLikeCountAndMarksLiked()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Test thread",
            Body = "Body",
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = new LikedDiscussionService(db);
        var result = await service.LikeAsync(user.Id, discussion.Id);

        Assert.True(result.IsLiked);
        Assert.Equal(1, result.LikeCount);
        Assert.Equal(1, await db.LikedDiscussions.CountAsync());
    }

    [Fact]
    public async Task UnlikeAsync_DecrementsLikeCount()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Test thread",
            Body = "Body",
            LikeCount = 1,
        };
        db.Discussions.Add(discussion);
        db.LikedDiscussions.Add(new LikedDiscussion { UserId = user.Id, DiscussionId = discussion.Id });
        await db.SaveChangesAsync();

        var service = new LikedDiscussionService(db);
        var result = await service.UnlikeAsync(user.Id, discussion.Id);

        Assert.False(result.IsLiked);
        Assert.Equal(0, result.LikeCount);
        Assert.Empty(db.LikedDiscussions);
    }
}

public class DiscussionEngagementTests
{
    [Fact]
    public async Task GetByIdAsync_IncrementsViewCount()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Views test",
            Body = "Body",
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var first = await service.GetByIdAsync(discussion.Id);
        var second = await service.GetByIdAsync(discussion.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first.ViewCount);
        Assert.Equal(2, second.ViewCount);
    }

    [Fact]
    public async Task ListAsync_ReturnsIsLikedForViewer()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Liked thread",
            Body = "Body",
            LikeCount = 1,
        };
        db.Discussions.Add(discussion);
        db.LikedDiscussions.Add(new LikedDiscussion { UserId = user.Id, DiscussionId = discussion.Id });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync(viewerUserId: user.Id);

        Assert.Single(results.Items);
        Assert.True(results.Items[0].IsLiked);
        Assert.Equal(1, results.Items[0].LikeCount);
    }

    [Fact]
    public async Task ListAsync_ReturnsIsLikedFalseWhenAnonymous()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread",
            Body = "Body",
            LikeCount = 3,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var results = await service.ListAsync(viewerUserId: null);

        Assert.Single(results.Items);
        Assert.False(results.Items[0].IsLiked);
    }

    [Fact]
    public void DiscussionDto_SerializesExplicitIsLikedFalse()
    {
        var dto = new DiscussionDto(
            Guid.NewGuid(),
            "T",
            "B",
            "Author",
            "AU",
            0,
            "now",
            null,
            0,
            0,
            IsLiked: false);

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        Assert.Contains("\"isLiked\":false", json);
        Assert.Contains("\"replyCount\":0", json);
    }

    private static DiscussionService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new DiscussionService(db, Mock.Of<IOpenAiService>(), moderation.Object, TestDbFactory.CreateMemoryCache());
    }
}
