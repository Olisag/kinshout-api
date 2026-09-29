using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
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
        var community = await TestDbFactory.SeedCommunityAsync(db, user);
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Old title",
            Body = "Old body",
            User = user,
            Category = category,
            Community = community,
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
        Assert.True(updated.IsCommunityMember);

        var stored = await db.Discussions.AsNoTracking().SingleAsync(d => d.Id == discussion.Id);
        Assert.Equal("New title", stored.Title);
        Assert.Equal("New body", stored.Body);
        Assert.Equal(community.Id, stored.CommunityId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateAsync_WithoutCommunity_PostsInGeneralAndJoinsAuthor(string? communitySlug)
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var openAi = new Mock<IOpenAiService>();
        openAi.Setup(o => o.AnalyzeDiscussionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Category>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDbFactory.SampleDiscussionAnalysis());
        var communities = new CommunityService(db, openAi.Object, Mock.Of<ICommunityJoinNotifier>());

        var service = CreateService(db, openAi.Object, communities);
        var created = await service.CreateAsync(
            user.Id,
            new CreateDiscussionRequestDto("Title", "Body", communitySlug));

        var general = await db.Communities.SingleAsync(c => c.Slug == CommunityDefaults.GeneralSlug);
        var stored = await db.Discussions.AsNoTracking().SingleAsync(d => d.Id == created.Id);
        Assert.Equal(general.Id, stored.CommunityId);
        Assert.True(created.IsCommunityMember);
        Assert.Contains(db.CommunityMembers, m =>
            m.CommunityId == general.Id && m.UserId == user.Id && m.Status == CommunityMemberStatuses.Approved);
    }

    [Fact]
    public async Task UpdateAsync_WithoutCommunitySlug_KeepsExistingCommunity()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var community = await TestDbFactory.SeedCommunityAsync(db, user, "gombe");
        var discussion = new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Old",
            Body = "Old",
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi.Setup(o => o.AnalyzeDiscussionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Category>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDbFactory.SampleDiscussionAnalysis());

        var service = CreateService(db, openAi.Object);
        await service.UpdateAsync(user.Id, discussion.Id, new("New", "New", CommunitySlug: " "));

        var stored = await db.Discussions.AsNoTracking().SingleAsync(d => d.Id == discussion.Id);
        Assert.Equal(community.Id, stored.CommunityId);
    }

    [Fact]
    public async Task SaveChanges_DiscussionWithoutCommunity_IsAssignedGeneral()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Discussions.Add(new Discussion { UserId = user.Id, CategoryId = category.Id, Title = "T", Body = "B" });
        await db.SaveChangesAsync();

        var general = await db.Communities.SingleAsync(c => c.Slug == CommunityDefaults.GeneralSlug);
        Assert.All(await db.Discussions.AsNoTracking().ToListAsync(), d => Assert.Equal(general.Id, d.CommunityId));

        var existing = await db.Discussions.SingleAsync();
        existing.CommunityId = null;
        await db.SaveChangesAsync();
        Assert.Equal(general.Id, (await db.Discussions.AsNoTracking().SingleAsync()).CommunityId);
        Assert.Single(db.Communities, c => c.Slug == CommunityDefaults.GeneralSlug);
    }

    [Fact]
    public async Task Sqlite_RejectsDiscussionWithoutCommunityAtDatabaseLevel()
    {
        await using var db = await TestDbFactory.CreateSqliteAsync();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Discussions (Id, UserId, CategoryId, Title, Body, CreatedAt, UpdatedAt)
            VALUES ({0}, {1}, {2}, 'T', 'B', '2026-01-01', '2026-01-01')
            """,
            Guid.NewGuid(), user.Id, category.Id));
        Assert.Contains("CommunityId is required", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_AssignsCommunity()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var community = await TestDbFactory.SeedCommunityAsync(db, user, "gombe", "Gombe");

        var openAi = new Mock<IOpenAiService>();
        openAi.Setup(o => o.AnalyzeDiscussionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Category>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDbFactory.SampleDiscussionAnalysis());

        var service = CreateService(db, openAi.Object);
        var created = await service.CreateAsync(
            user.Id,
            new CreateDiscussionRequestDto("Title", "Body about Kinshasa", "gombe"));

        Assert.Equal("k/gombe", created.CommunitySlug);
        Assert.True(created.IsCommunityMember);
        var stored = await db.Discussions.SingleAsync();
        Assert.Equal(community.Id, stored.CommunityId);
        Assert.Equal("societe", stored.TopicSlug);
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

    private static DiscussionService CreateService(
        KinshoutDbContext db,
        IOpenAiService? openAi = null,
        ICommunityService? communities = null)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        openAi ??= Mock.Of<IOpenAiService>();
        return new DiscussionService(
            db,
            openAi,
            moderation.Object,
            Mock.Of<IUploadStorage>(),
            communities ?? TestDbFactory.CreatePermissiveCommunityService(),
            TestDbFactory.CreatePermissiveDiscussionParticipationService(),
            TestDbFactory.CreatePermissiveVideoService(),
            TestDbFactory.CreateMemoryCache());
    }
}
