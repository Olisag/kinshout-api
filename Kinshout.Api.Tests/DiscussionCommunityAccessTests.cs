using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionCommunityAccessTests
{
    [Fact]
    public async Task GetByIdAsync_PublicDiscussionInPublicCommunity_IsReadableByOutsiderAndAnonymous()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "open");
        var discussion = await AddDiscussionAsync(db, creator, category, community, "Open thread", CommunityVisibilities.Public);

        var service = CreateService(db);

        Assert.Equal("Open thread", (await service.GetByIdAsync(discussion.Id, viewerUserId: outsider.Id))!.Title);
        Assert.Equal("Open thread", (await service.GetByIdAsync(discussion.Id, viewerUserId: null))!.Title);
    }

    [Fact]
    public async Task GetByIdAsync_PrivateDiscussionInPublicCommunity_IsBlockedForOutsider()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "open");
        var discussion = await AddDiscussionAsync(db, creator, category, community, "Hidden thread", CommunityVisibilities.Private);

        var service = CreateService(db);

        var denied = await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.GetByIdAsync(discussion.Id, viewerUserId: outsider.Id));
        Assert.Equal(DiscussionJoinPrompt.DiscussionMembershipRequired, denied.JoinPrompt.Code);
        Assert.False(denied.JoinPrompt.RequiresSignIn);
        Assert.Null(denied.JoinPrompt.JoinCommunityUrl);

        var anonymous = await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.GetByIdAsync(discussion.Id, viewerUserId: null));
        Assert.True(anonymous.JoinPrompt.RequiresSignIn);
        Assert.NotNull(await service.GetByIdAsync(discussion.Id, viewerUserId: creator.Id));
    }

    [Fact]
    public async Task GetByIdAsync_PublicDiscussionInPrivateCommunity_IsReadOnlyForOutsider()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "secret");
        community.Visibility = CommunityVisibilities.Private;
        var discussion = await AddDiscussionAsync(db, creator, category, community, "Members thread", CommunityVisibilities.Public);
        db.DiscussionReplies.Add(new DiscussionReply { DiscussionId = discussion.Id, UserId = creator.Id, Body = "First reply" });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var detail = await service.GetByIdAsync(discussion.Id, viewerUserId: outsider.Id);
        Assert.NotNull(detail);
        Assert.Equal("Members thread", detail!.Title);
        Assert.Equal(["First reply"], detail.Thread.Items.Select(r => r.Text).ToArray());
        Assert.True(detail.CanAccess);
        Assert.False(detail.CanParticipate);
        var prompt = detail.JoinPrompt;
        Assert.NotNull(prompt);
        Assert.Equal(DiscussionJoinPrompt.CommunityMembershipRequired, prompt!.Code);
        Assert.False(prompt.RequiresSignIn);
        Assert.Equal("secret", prompt.Community?.Slug);
        Assert.Equal("/api/communities/secret/join", prompt.JoinCommunityUrl);
        Assert.Equal($"/api/discussions/{discussion.Id}/join", prompt.JoinDiscussionUrl);

        var anonymous = await service.GetByIdAsync(discussion.Id, viewerUserId: null);
        Assert.False(anonymous!.CanParticipate);
        Assert.True(anonymous.JoinPrompt!.RequiresSignIn);

        Assert.Null((await service.GetByIdAsync(discussion.Id, viewerUserId: creator.Id))!.JoinPrompt);
    }

    [Fact]
    public async Task AddReplyAsync_PublicDiscussionInPrivateCommunity_AsksOutsiderToJoin()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "secret");
        community.Visibility = CommunityVisibilities.Private;
        var discussion = await AddDiscussionAsync(db, creator, category, community, "Members thread", CommunityVisibilities.Public);

        var service = CreateService(db);

        var denied = await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.AddReplyAsync(outsider.Id, discussion.Id, new CreateReplyRequestDto("Hello")));
        Assert.Equal(DiscussionJoinPrompt.CommunityMembershipRequired, denied.JoinPrompt.Code);
        Assert.Equal("/api/communities/secret/join", denied.JoinPrompt.JoinCommunityUrl);
        Assert.Equal(0, await db.DiscussionReplies.CountAsync());
    }

    [Fact]
    public async Task GetByIdAsync_PrivateDiscussionInPrivateCommunity_IsBlockedWithCommunityPrompt()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "secret");
        community.Visibility = CommunityVisibilities.Private;
        var discussion = await AddDiscussionAsync(db, creator, category, community, "Hidden thread", CommunityVisibilities.Private);

        var service = CreateService(db);

        var denied = await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.GetByIdAsync(discussion.Id, viewerUserId: outsider.Id));
        Assert.Equal(DiscussionJoinPrompt.CommunityMembershipRequired, denied.JoinPrompt.Code);
        Assert.Equal("/api/communities/secret/join", denied.JoinPrompt.JoinCommunityUrl);
    }

    [Fact]
    public async Task ListAsync_MainFeed_HidesPrivateCommunityDiscussionsFromNonMembers()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var member = await AddUserAsync(db, "member");
        var open = await TestDbFactory.SeedCommunityAsync(db, creator, "open");
        var secret = await TestDbFactory.SeedCommunityAsync(db, creator, "secret");
        secret.Visibility = CommunityVisibilities.Private;
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = secret.Id,
            UserId = member.Id,
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
        });
        await AddDiscussionAsync(db, creator, category, open, "Open public", CommunityVisibilities.Public);
        await AddDiscussionAsync(db, creator, category, secret, "Secret public", CommunityVisibilities.Public);

        var service = CreateService(db);

        var asAnonymous = await service.ListAsync(viewerUserId: null);
        var asOutsider = await service.ListAsync(viewerUserId: outsider.Id);
        var asMember = await service.ListAsync(viewerUserId: member.Id);

        Assert.Equal(["Open public"], asAnonymous.Items.Select(d => d.Title).ToArray());
        Assert.Equal(["Open public"], asOutsider.Items.Select(d => d.Title).ToArray());
        Assert.Equal(1, asOutsider.TotalCount);
        Assert.Equal(2, asMember.TotalCount);
    }

    [Fact]
    public async Task ListPublicByUserAsync_HidesPrivateCommunityDiscussionsFromNonMembers()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        creator.IsProfilePublic = true;
        var outsider = await AddUserAsync(db, "outsider");
        var open = await TestDbFactory.SeedCommunityAsync(db, creator, "open");
        var secret = await TestDbFactory.SeedCommunityAsync(db, creator, "secret");
        secret.Visibility = CommunityVisibilities.Private;
        await AddDiscussionAsync(db, creator, category, open, "Open public", CommunityVisibilities.Public);
        await AddDiscussionAsync(db, creator, category, secret, "Secret public", CommunityVisibilities.Public);

        var service = CreateService(db);

        var asOutsider = await service.ListPublicByUserAsync(creator.Id, viewerUserId: outsider.Id);
        var asAnonymous = await service.ListPublicByUserAsync(creator.Id, viewerUserId: null);
        var asCreator = await service.ListPublicByUserAsync(creator.Id, viewerUserId: creator.Id);

        Assert.Equal(["Open public"], asOutsider.Items.Select(d => d.Title).ToArray());
        Assert.Equal(["Open public"], asAnonymous.Items.Select(d => d.Title).ToArray());
        Assert.Equal(2, asCreator.TotalCount);
    }

    [Fact]
    public async Task ListAsync_ByPublicCommunity_HidesPrivateDiscussionsFromNonMembers()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = await AddUserAsync(db, "outsider");
        var member = await AddUserAsync(db, "member");
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "open");
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = member.Id,
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
        });
        await AddDiscussionAsync(db, creator, category, community, "Public one", CommunityVisibilities.Public);
        await AddDiscussionAsync(db, creator, category, community, "Private one", CommunityVisibilities.Private);

        var service = CreateService(db);

        var asAnonymous = await service.ListAsync(communitySlug: "open", viewerUserId: null);
        var asOutsider = await service.ListAsync(communitySlug: "open", viewerUserId: outsider.Id);
        var asMember = await service.ListAsync(communitySlug: "open", viewerUserId: member.Id);

        Assert.Equal(["Public one"], asAnonymous.Items.Select(d => d.Title).ToArray());
        Assert.Equal(["Public one"], asOutsider.Items.Select(d => d.Title).ToArray());
        Assert.Equal(2, asMember.TotalCount);
    }

    private static async Task<User> AddUserAsync(KinshoutDbContext db, string name)
    {
        var user = new User { Email = $"{name}@test", DisplayName = name };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Discussion> AddDiscussionAsync(
        KinshoutDbContext db,
        User author,
        Category category,
        Community community,
        string title,
        string visibility)
    {
        var discussion = new Discussion
        {
            UserId = author.Id,
            User = author,
            CategoryId = category.Id,
            Category = category,
            CommunityId = community.Id,
            Community = community,
            Title = title,
            Body = title,
            Visibility = visibility,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();
        return discussion;
    }

    private static DiscussionService CreateService(KinshoutDbContext db)
    {
        var moderation = new Mock<IAdvertModerationService>();
        moderation.Setup(m => m.EnsureTextAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var communities = new CommunityService(db, Mock.Of<IOpenAiService>(), Mock.Of<ICommunityJoinNotifier>());
        var participation = new DiscussionParticipationService(db, communities, Mock.Of<IDiscussionJoinNotifier>());

        return new DiscussionService(
            db,
            Mock.Of<IOpenAiService>(),
            moderation.Object,
            Mock.Of<IUploadStorage>(),
            communities,
            participation,
            TestDbFactory.CreatePermissiveVideoService(),
            TestDbFactory.CreateMemoryCache());
    }
}
