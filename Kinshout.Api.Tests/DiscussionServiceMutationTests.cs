using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionServiceMutationTests
{
    [Fact]
    public async Task UpdateAsync_Owner_UpdatesDiscussion()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Old title",
            Body = "Old body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi.Setup(o => o.AnalyzeDiscussionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Category>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDbFactory.SampleDiscussionAnalysis());

        var service = CreateService(db, openAi.Object);
        var updated = await service.UpdateAsync(user.Id, discussion.Id, new("New title", "New body"));

        Assert.Equal("New title", updated.Title);
        Assert.Equal("New body", updated.Body);
        Assert.Equal(user.Id, updated.AuthorId);

        var stored = await db.Discussions.AsNoTracking().SingleAsync(d => d.Id == discussion.Id);
        Assert.Equal("New title", stored.Title);
        Assert.Equal("New body", stored.Body);
    }

    [Fact]
    public async Task UpdateAsync_OtherUser_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@test", DisplayName = "Other" };
        db.Users.Add(other);

        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Mine",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateAsync(other.Id, discussion.Id, new("Hack", "Hack")));
    }

    [Fact]
    public async Task DeleteAsync_Owner_RemovesDiscussionAndReplies()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "To delete",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);
        db.DiscussionReplies.Add(new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = user.Id,
            Body = "Reply",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DeleteAsync(user.Id, discussion.Id);

        Assert.False(await db.Discussions.AnyAsync(d => d.Id == discussion.Id));
        Assert.False(await db.DiscussionReplies.AnyAsync(r => r.DiscussionId == discussion.Id));
    }

    [Fact]
    public async Task DeleteAsync_OtherUser_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@test", DisplayName = "Other" };
        db.Users.Add(other);

        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Mine",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(other.Id, discussion.Id));
    }

    [Fact]
    public async Task UpdateReplyAsync_Owner_UpdatesReply()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);

        var reply = new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = user.Id,
            Body = "Old reply",
        };
        db.DiscussionReplies.Add(reply);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var updated = await service.UpdateReplyAsync(user.Id, discussion.Id, reply.Id, new("New reply"));

        Assert.Equal("New reply", updated.Text);
        Assert.Equal(user.Id, updated.AuthorId);

        var stored = await db.DiscussionReplies.AsNoTracking().SingleAsync(r => r.Id == reply.Id);
        Assert.Equal("New reply", stored.Body);
    }

    [Fact]
    public async Task UpdateReplyAsync_OtherUser_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@test", DisplayName = "Other" };
        db.Users.Add(other);

        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);

        var reply = new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = user.Id,
            Body = "Reply",
        };
        db.DiscussionReplies.Add(reply);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateReplyAsync(other.Id, discussion.Id, reply.Id, new("Hack")));
    }

    [Fact]
    public async Task DeleteReplyAsync_Owner_RemovesReply()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);

        var reply = new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = user.Id,
            Body = "Reply",
        };
        db.DiscussionReplies.Add(reply);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DeleteReplyAsync(user.Id, discussion.Id, reply.Id);

        Assert.False(await db.DiscussionReplies.AnyAsync(r => r.Id == reply.Id));
        Assert.True(await db.Discussions.AnyAsync(d => d.Id == discussion.Id));
    }

    [Fact]
    public async Task DeleteReplyAsync_OtherUser_Throws()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@test", DisplayName = "Other" };
        db.Users.Add(other);

        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Thread",
            Body = "Body",
            User = user,
            Category = category,
        };
        db.Discussions.Add(discussion);

        var reply = new DiscussionReply
        {
            DiscussionId = discussion.Id,
            UserId = user.Id,
            Body = "Reply",
        };
        db.DiscussionReplies.Add(reply);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeleteReplyAsync(other.Id, discussion.Id, reply.Id));
    }

    private static DiscussionService CreateService(KinshoutDbContext db, IOpenAiService? openAi = null)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        openAi ??= Mock.Of<IOpenAiService>();
        return new DiscussionService(db, openAi, moderation.Object, TestDbFactory.CreateMemoryCache());
    }
}
