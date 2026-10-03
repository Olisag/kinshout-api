using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class DiscussionParticipationServiceTests
{
    [Fact]
    public async Task RequestJoinAsync_GrantsImmediateAccessForPublicDiscussion()
    {
        await using var db = TestDbFactory.Create();
        var (author, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);

        var discussion = new Discussion
        {
            UserId = author.Id,
            CategoryId = category.Id,
            Title = "Open thread",
            Body = "Body",
            Visibility = CommunityVisibilities.Public,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        var participant = Assert.Single(db.DiscussionParticipants.Where(p => p.UserId == joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Approved, participant.Status);
        await service.EnsureCanParticipateAsync(discussion, joiner.Id);
    }

    [Fact]
    public async Task RequestJoinAsync_CreatesPendingRequestForPrivateDiscussion()
    {
        await using var db = TestDbFactory.Create();
        var (author, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);

        var discussion = new Discussion
        {
            UserId = author.Id,
            CategoryId = category.Id,
            Title = "Private thread",
            Body = "Body",
            Visibility = CommunityVisibilities.Private,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var notifier = new Mock<IDiscussionJoinNotifier>();
        var service = CreateService(db, notifier);
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        var participant = Assert.Single(db.DiscussionParticipants.Where(p => p.UserId == joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Pending, participant.Status);
        notifier.Verify(
            n => n.NotifyJoinRequestAsync(
                It.Is<Discussion>(d => d.Id == discussion.Id),
                It.Is<User>(u => u.Id == joiner.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnsureCanViewAsync_BlocksPrivateDiscussionForNonMembers()
    {
        await using var db = TestDbFactory.Create();
        var (author, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = new User { Email = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(outsider);

        var discussion = new Discussion
        {
            UserId = author.Id,
            CategoryId = category.Id,
            Title = "Secret",
            Body = "Body",
            Visibility = CommunityVisibilities.Private,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var denied = await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.EnsureCanViewAsync(discussion, outsider.Id));
        Assert.Equal(DiscussionJoinPrompt.DiscussionMembershipRequired, denied.JoinPrompt.Code);
        Assert.Null(denied.JoinPrompt.JoinCommunityUrl);
        Assert.Equal($"/api/discussions/{discussion.Id}/join", denied.JoinPrompt.JoinDiscussionUrl);
    }

    [Fact]
    public async Task ApproveParticipantAsync_CommunityPrivateDiscussion_RequiresCreatorOrModerator()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var author = new User { Email = "author@test", DisplayName = "Author" };
        var moderator = new User { Email = "mod@test", DisplayName = "Mod" };
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.AddRange(author, moderator, joiner);

        var community = new Community
        {
            Slug = "club",
            Name = "Club",
            CreatedByUserId = creator.Id,
            Visibility = CommunityVisibilities.Public,
        };
        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = creator.Id,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
        });
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = moderator.Id,
            Role = CommunityMemberRoles.Moderator,
            Status = CommunityMemberStatuses.Approved,
        });
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = author.Id,
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
        });

        var discussion = new Discussion
        {
            UserId = author.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Private in community",
            Body = "Body",
            Visibility = CommunityVisibilities.Private,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db, communityService: CreateCommunityService(db));
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ApproveParticipantAsync(author.Id, discussion.Id, joiner.Id));

        await service.ApproveParticipantAsync(moderator.Id, discussion.Id, joiner.Id);

        var participant = await db.DiscussionParticipants.SingleAsync(p => p.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Approved, participant.Status);
        Assert.Equal(moderator.Id, participant.ReviewedByUserId);
        await service.EnsureCanParticipateAsync(discussion, joiner.Id);

        var communityMembership = await db.CommunityMembers.SingleAsync(m => m.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Approved, communityMembership.Status);
    }

    [Fact]
    public async Task PrivateDiscussionInGeneral_StaysPrivateAndItsAuthorApprovesParticipants()
    {
        await using var db = TestDbFactory.Create();
        var (author, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();
        var general = await CommunitySeed.EnsureGeneralCommunityAsync(db);
        await CommunitySeed.JoinGeneralCommunityAsync(db, author.Id);
        await CommunitySeed.JoinGeneralCommunityAsync(db, joiner.Id);

        var discussion = new Discussion
        {
            UserId = author.Id,
            CategoryId = category.Id,
            CommunityId = general.Id,
            Title = "Private in general",
            Body = "Body",
            Visibility = CommunityVisibilities.Private,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db, communityService: CreateCommunityService(db));
        await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.EnsureCanViewAsync(discussion, joiner.Id));
        Assert.False(await DiscussionVisibilityFilter
            .WhereVisibleTo(db.Discussions, db, joiner.Id)
            .AnyAsync(d => d.Id == discussion.Id));

        await service.RequestJoinAsync(joiner.Id, discussion.Id);
        await service.ApproveParticipantAsync(author.Id, discussion.Id, joiner.Id);

        await service.EnsureCanViewAsync(discussion, joiner.Id);
        await service.EnsureCanParticipateAsync(discussion, joiner.Id);
        Assert.True(await DiscussionVisibilityFilter
            .WhereVisibleTo(db.Discussions, db, joiner.Id)
            .AnyAsync(d => d.Id == discussion.Id));
    }

    [Fact]
    public async Task ApproveParticipantAsync_PrivateCommunity_GrantsFullCommunityAccess()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);

        var community = new Community
        {
            Slug = "secret-club",
            Name = "Secret Club",
            CreatedByUserId = creator.Id,
            Visibility = CommunityVisibilities.Private,
        };
        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = creator.Id,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
        });

        var discussion = new Discussion
        {
            UserId = creator.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Private thread",
            Body = "Body",
            Visibility = CommunityVisibilities.Private,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var communityService = CreateCommunityService(db);
        var service = CreateService(db, communityService: communityService);
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        var pendingCommunity = await db.CommunityMembers.SingleAsync(m => m.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Pending, pendingCommunity.Status);

        await service.ApproveParticipantAsync(creator.Id, discussion.Id, joiner.Id);

        var communityMembership = await db.CommunityMembers.SingleAsync(m => m.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Approved, communityMembership.Status);
        Assert.Equal(creator.Id, communityMembership.ReviewedByUserId);

        var dto = await communityService.GetBySlugAsync("secret-club", joiner.Id);
        Assert.NotNull(dto);
        Assert.True(dto!.CanAccess);
        Assert.True(dto.CanPost);
    }

    [Fact]
    public async Task RequestJoinAsync_PublicDiscussionInPrivateCommunity_RequiresApproval()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);

        var community = new Community
        {
            Slug = "secret-club",
            Name = "Secret Club",
            CreatedByUserId = creator.Id,
            Visibility = CommunityVisibilities.Private,
        };
        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = creator.Id,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
        });

        var discussion = new Discussion
        {
            UserId = creator.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Open thread",
            Body = "Body",
            Visibility = CommunityVisibilities.Public,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var communityService = CreateCommunityService(db);
        var service = CreateService(db, communityService: communityService);
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        var communityMembership = await db.CommunityMembers.AsNoTracking().SingleAsync(m => m.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Pending, communityMembership.Status);
        var participant = await db.DiscussionParticipants.AsNoTracking().SingleAsync(p => p.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Pending, participant.Status);

        await service.EnsureCanViewAsync(discussion, joiner.Id);
        var denied = await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.EnsureCanParticipateAsync(discussion, joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Pending, denied.JoinPrompt.ViewerStatus);

        await communityService.ApproveMemberAsync(creator.Id, "secret-club", joiner.Id);

        // Joining again once the community approved the membership activates participation.
        await service.RequestJoinAsync(joiner.Id, discussion.Id);
        participant = await db.DiscussionParticipants.AsNoTracking().SingleAsync(p => p.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Approved, participant.Status);

        var dto = await communityService.GetBySlugAsync("secret-club", joiner.Id);
        Assert.NotNull(dto);
        Assert.True(dto!.CanAccess);
        Assert.True(dto.CanPost);
    }

    [Fact]
    public async Task RequestJoinAsync_PublicDiscussionInPublicCommunity_GrantsAccessImmediately()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);
        var community = await TestDbFactory.SeedCommunityAsync(db, creator, "open-club");
        var discussion = new Discussion
        {
            UserId = creator.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Open thread",
            Body = "Body",
            Visibility = CommunityVisibilities.Public,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db, communityService: CreateCommunityService(db));
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        Assert.Equal(
            CommunityMemberStatuses.Approved,
            (await db.CommunityMembers.AsNoTracking().SingleAsync(m => m.UserId == joiner.Id)).Status);
        Assert.Equal(
            CommunityMemberStatuses.Approved,
            (await db.DiscussionParticipants.AsNoTracking().SingleAsync(p => p.UserId == joiner.Id)).Status);
    }

    [Fact]
    public async Task AddReplyAsync_RequiresJoinForPublicDiscussionWhenNotAuthor()
    {
        await using var db = TestDbFactory.Create();
        var (author, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);

        var discussion = new Discussion
        {
            UserId = author.Id,
            CategoryId = category.Id,
            Title = "Open",
            Body = "Body",
            Visibility = CommunityVisibilities.Public,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var participation = CreateService(db);
        await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            participation.EnsureCanParticipateAsync(discussion, joiner.Id));

        await participation.RequestJoinAsync(joiner.Id, discussion.Id);
        await participation.EnsureCanParticipateAsync(discussion, joiner.Id);
    }

    [Fact]
    public async Task RequestJoinAsync_ImplicitlyJoinsCommunityWhenDiscussionBelongsToOne()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);

        var community = new Community
        {
            Slug = "open-club",
            Name = "Open Club",
            CreatedByUserId = creator.Id,
            Visibility = CommunityVisibilities.Public,
        };
        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = creator.Id,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
        });

        var discussion = new Discussion
        {
            UserId = creator.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Community thread",
            Body = "Body",
            Visibility = CommunityVisibilities.Public,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var communityService = CreateCommunityService(db);
        var service = CreateService(db, communityService: communityService);
        await service.RequestJoinAsync(joiner.Id, discussion.Id);

        var communityMembership = Assert.Single(db.CommunityMembers.Where(m => m.UserId == joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Approved, communityMembership.Status);
        var discussionMembership = Assert.Single(db.DiscussionParticipants.Where(p => p.UserId == joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Approved, discussionMembership.Status);
    }

    [Fact]
    public async Task EnsureCanViewAsync_AllowsCommunityMemberToViewPrivateDiscussionWithoutJoining()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var member = new User { Email = "member@test", DisplayName = "Member" };
        db.Users.Add(member);

        var community = new Community
        {
            Slug = "club",
            Name = "Club",
            CreatedByUserId = creator.Id,
            Visibility = CommunityVisibilities.Public,
        };
        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = creator.Id,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
        });
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = member.Id,
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
        });

        var discussion = new Discussion
        {
            UserId = creator.Id,
            CategoryId = category.Id,
            CommunityId = community.Id,
            Title = "Private in community",
            Body = "Body",
            Visibility = CommunityVisibilities.Private,
        };
        db.Discussions.Add(discussion);
        await db.SaveChangesAsync();

        var service = CreateService(db, communityService: CreateCommunityService(db));
        await service.EnsureCanViewAsync(discussion, member.Id);
        await Assert.ThrowsAsync<DiscussionAccessDeniedException>(() =>
            service.EnsureCanParticipateAsync(discussion, member.Id));
    }

    private static CommunityService CreateCommunityService(KinshoutDbContext db) =>
        new(db, Mock.Of<IOpenAiService>(), Mock.Of<ICommunityJoinNotifier>());

    private static DiscussionParticipationService CreateService(
        KinshoutDbContext db,
        Mock<IDiscussionJoinNotifier>? notifier = null,
        ICommunityService? communityService = null)
    {
        notifier ??= new Mock<IDiscussionJoinNotifier>();
        return new DiscussionParticipationService(
            db,
            communityService ?? TestDbFactory.CreatePermissiveCommunityService(),
            notifier.Object);
    }
}
