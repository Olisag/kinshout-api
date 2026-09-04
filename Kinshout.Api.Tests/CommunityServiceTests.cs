using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinshout.Api.Tests;

public class CommunityServiceTests
{
    [Fact]
    public async Task CreateAsync_StoresSlugWithoutKPrefixAndCreatorMembership()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        var created = await service.CreateAsync(
            user.Id,
            new CreateCommunityRequestDto("k/gombe-news", "Gombe News", Visibility: CommunityVisibilities.Public));

        Assert.Equal("gombe-news", created.Slug);
        Assert.Equal("k/gombe-news", created.RouteSlug);
        Assert.Equal(CommunityVisibilities.Public, created.Visibility);
        Assert.True(created.IsActive);
        Assert.True(created.CanAccess);
        Assert.True(created.CanPost);
        Assert.True(created.CanModerate);
        Assert.Equal(0, created.ModeratorCount);

        var stored = Assert.Single(db.Communities);
        Assert.Equal("gombe-news", stored.Slug);
        var membership = Assert.Single(db.CommunityMembers);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(CommunityMemberRoles.Creator, membership.Role);
        Assert.Equal(CommunityMemberStatuses.Approved, membership.Status);
    }

    [Fact]
    public async Task DeleteAsync_SucceedsWhenEmpty()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        await service.CreateAsync(user.Id, new CreateCommunityRequestDto("empty-community"));

        await service.DeleteAsync(user.Id, "k/empty-community");

        Assert.Empty(db.Communities);
        Assert.Empty(db.CommunityMembers);
    }

    [Fact]
    public async Task DeleteAsync_FailsWhenDiscussionsExist()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
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
    public async Task ListAsync_OrdersByRecentByDefault()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);

        var older = await service.CreateAsync(user.Id, new CreateCommunityRequestDto("older"));
        await Task.Delay(5);
        var newer = await service.CreateAsync(user.Id, new CreateCommunityRequestDto("newer"));

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = category.Id,
            CommunityId = older.Id,
            Title = "Post",
            Body = "Body",
        });
        await db.SaveChangesAsync();

        var results = await service.ListAsync(sort: ListSortHelper.Recent, viewerUserId: user.Id);

        Assert.Equal(["newer", "older"], results.Items.Select(c => c.Slug).ToArray());
    }

    [Fact]
    public async Task ListAsync_HidesPrivateCommunitiesFromAnonymousViewers()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var service = CreateService(db);
        await service.CreateAsync(user.Id, new CreateCommunityRequestDto("public-one", Visibility: CommunityVisibilities.Public));
        await service.CreateAsync(user.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));

        var results = await service.ListAsync();

        Assert.Single(results.Items);
        Assert.Equal("public-one", results.Items[0].Slug);
    }

    [Fact]
    public async Task RequestJoinAsync_GrantsImmediateAccessForPublicCommunity()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("open-club", Visibility: CommunityVisibilities.Public));
        await service.RequestJoinAsync(joiner.Id, "open-club");

        var membership = Assert.Single(db.CommunityMembers.Where(m => m.UserId == joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Approved, membership.Status);

        var dto = await service.GetBySlugAsync("open-club", joiner.Id);
        Assert.True(dto!.CanAccess);
        Assert.True(dto.CanPost);
    }

    [Fact]
    public async Task RequestJoinAsync_CreatesPendingRequestForPrivateCommunity()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();

        var notifier = new Mock<ICommunityJoinNotifier>();
        var service = CreateService(db, notifier);

        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));
        await service.RequestJoinAsync(joiner.Id, "secret");

        var membership = Assert.Single(db.CommunityMembers.Where(m => m.UserId == joiner.Id));
        Assert.Equal(CommunityMemberStatuses.Pending, membership.Status);
        notifier.Verify(
            n => n.NotifyJoinRequestAsync(It.IsAny<Community>(), It.Is<User>(u => u.Id == joiner.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InviteMemberAsync_AllowsAccessToPrivateCommunity()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var invitee = new User { Email = "invitee@test", DisplayName = "Invitee" };
        db.Users.Add(invitee);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));
        await service.InviteMemberAsync(creator.Id, "secret", invitee.Id);

        var dto = await service.GetBySlugAsync("secret", invitee.Id);
        Assert.NotNull(dto);
        Assert.True(dto!.CanAccess);
    }

    [Fact]
    public async Task ApproveMemberAsync_ModeratorApprovalSufficesWithoutCreator()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var moderator = new User { Email = "mod@test", DisplayName = "Mod" };
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.AddRange(moderator, joiner);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));
        await service.InviteMemberAsync(creator.Id, "secret", moderator.Id);
        await service.AddModeratorAsync(creator.Id, "secret", moderator.Id);
        await service.RequestJoinAsync(joiner.Id, "secret");

        await service.ApproveMemberAsync(moderator.Id, "secret", joiner.Id);

        var membership = await db.CommunityMembers.SingleAsync(m => m.UserId == joiner.Id);
        Assert.Equal(CommunityMemberStatuses.Approved, membership.Status);
        Assert.Equal(moderator.Id, membership.ReviewedByUserId);

        var dto = await service.GetBySlugAsync("secret", joiner.Id);
        Assert.NotNull(dto);
        Assert.True(dto!.CanAccess);
    }

    [Fact]
    public async Task ApproveMemberAsync_GrantsAccessAndNotifiesUser()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var joiner = new User { Email = "joiner@test", DisplayName = "Joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();

        var notifier = new Mock<ICommunityJoinNotifier>();
        var service = CreateService(db, notifier);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));
        await service.RequestJoinAsync(joiner.Id, "secret");
        await service.ApproveMemberAsync(creator.Id, "secret", joiner.Id);

        var dto = await service.GetBySlugAsync("secret", joiner.Id);
        Assert.NotNull(dto);
        Assert.True(dto!.CanAccess);
        Assert.Equal(CommunityMemberStatuses.Approved, dto.ViewerStatus);
        notifier.Verify(
            n => n.NotifyJoinApprovedAsync(It.IsAny<Community>(), It.Is<User>(u => u.Id == joiner.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetBySlugAsync_ReturnsNullForPrivateCommunityWhenNotMember()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = new User { Email = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));

        var dto = await service.GetBySlugAsync("secret", outsider.Id);
        Assert.Null(dto);
    }

    [Fact]
    public async Task AddModeratorAsync_AllowsUpToFourModerators()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var mods = Enumerable.Range(0, 5).Select(i => new User
        {
            Email = $"mod{i}@test",
            DisplayName = $"Mod {i}",
        }).ToList();
        db.Users.AddRange(mods);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("modded"));

        foreach (var mod in mods.Take(4))
        {
            await service.InviteMemberAsync(creator.Id, "modded", mod.Id);
            await service.AddModeratorAsync(creator.Id, "modded", mod.Id);
        }

        var community = await service.GetBySlugAsync("modded", creator.Id);
        Assert.Equal(4, community!.ModeratorCount);

        await service.InviteMemberAsync(creator.Id, "modded", mods[4].Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddModeratorAsync(creator.Id, "modded", mods[4].Id));
    }

    [Fact]
    public async Task AddModeratorAsync_InvitesThenPromotesMember()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var mod = new User { Email = "mod@test", DisplayName = "Mod" };
        db.Users.Add(mod);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("team"));
        await service.InviteMemberAsync(creator.Id, "team", mod.Id);
        await service.AddModeratorAsync(creator.Id, "team", mod.Id);

        var membership = await db.CommunityMembers.SingleAsync(m => m.UserId == mod.Id);
        Assert.Equal(CommunityMemberRoles.Moderator, membership.Role);
    }

    [Fact]
    public async Task CreatorLeave_TransfersOwnershipToModerator()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var moderator = new User { Email = "mod@test", DisplayName = "Mod" };
        db.Users.Add(moderator);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("handoff"));
        await service.InviteMemberAsync(creator.Id, "handoff", moderator.Id);
        await service.AddModeratorAsync(creator.Id, "handoff", moderator.Id);

        await service.LeaveAsync(creator.Id, "handoff");

        var community = await db.Communities.SingleAsync();
        Assert.Equal(moderator.Id, community.CreatedByUserId);
        Assert.True(community.IsActive);

        var successor = await db.CommunityMembers.SingleAsync(m => m.UserId == moderator.Id);
        Assert.Equal(CommunityMemberRoles.Creator, successor.Role);
        Assert.DoesNotContain(db.CommunityMembers, m => m.UserId == creator.Id);
    }

    [Fact]
    public async Task CreatorLeave_WithoutModeratorsSetsCommunityInactive()
    {
        await using var db = TestDbFactory.Create();
        var (creator, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var member = new User { Email = "member@test", DisplayName = "Member" };
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var created = await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("archive"));
        await service.InviteMemberAsync(creator.Id, "archive", member.Id);

        await service.LeaveAsync(creator.Id, "archive");

        var community = await db.Communities.SingleAsync();
        Assert.False(community.IsActive);

        var dto = await service.GetBySlugAsync("archive", member.Id);
        Assert.NotNull(dto);
        Assert.True(dto!.CanAccess);
        Assert.False(dto.CanPost);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EnsureCanPostAsync(created.Id, member.Id));
    }

    [Fact]
    public async Task MemberLeave_RemovesMembershipAnytime()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var member = new User { Email = "member@test", DisplayName = "Member" };
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("open"));
        await service.InviteMemberAsync(creator.Id, "open", member.Id);
        await service.LeaveAsync(member.Id, "open");

        Assert.DoesNotContain(db.CommunityMembers, m => m.UserId == member.Id);
    }

    [Fact]
    public async Task EnsureCanAccessAsync_BlocksNonMembersOnPublicCommunity()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = new User { Email = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var created = await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("open-club"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EnsureCanAccessAsync(created.Id, outsider.Id));
    }

    [Fact]
    public async Task DeleteAsync_FailsForNonCreator()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var other = new User { Email = "other@test", DisplayName = "Other" };
        db.Users.Add(other);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(user.Id, new CreateCommunityRequestDto("mine"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DeleteAsync(other.Id, "mine"));
    }

    [Fact]
    public async Task ListMembersAsync_ReturnsApprovedMembersOnlyOrderedByRole()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var moderator = new User { Email = "mod@test", DisplayName = "Mod" };
        var member = new User { Email = "member@test", DisplayName = "Member" };
        var pending = new User { Email = "pending@test", DisplayName = "Pending" };
        db.Users.AddRange(moderator, member, pending);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("club"));
        await service.InviteMemberAsync(creator.Id, "club", moderator.Id);
        await service.AddModeratorAsync(creator.Id, "club", moderator.Id);
        await service.RequestJoinAsync(member.Id, "club");

        var communityId = (await db.Communities.SingleAsync(c => c.Slug == "club")).Id;
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = communityId,
            UserId = pending.Id,
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Pending,
        });
        await db.SaveChangesAsync();

        var result = await service.ListMembersAsync("club");

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            [CommunityMemberRoles.Creator, CommunityMemberRoles.Moderator, CommunityMemberRoles.Member],
            result.Items.Select(m => m.Role).ToArray());
        Assert.DoesNotContain(result.Items, m => m.UserId == pending.Id);
        Assert.All(result.Items, m => Assert.Equal(CommunityMemberStatuses.Approved, m.Status));
    }

    [Fact]
    public async Task ListModeratorsAsync_ReturnsCreatorAndModeratorsOnly()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var moderator = new User { Email = "mod@test", DisplayName = "Mod" };
        var member = new User { Email = "member@test", DisplayName = "Member" };
        db.Users.AddRange(moderator, member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("club"));
        await service.InviteMemberAsync(creator.Id, "club", moderator.Id);
        await service.AddModeratorAsync(creator.Id, "club", moderator.Id);
        await service.RequestJoinAsync(member.Id, "club");

        var result = await service.ListModeratorsAsync("k/club");

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(
            [CommunityMemberRoles.Creator, CommunityMemberRoles.Moderator],
            result.Items.Select(m => m.Role).ToArray());
        Assert.DoesNotContain(result.Items, m => m.UserId == member.Id);
    }

    [Fact]
    public async Task ListMembersAsync_PrivateCommunity_RequiresMembership()
    {
        await using var db = TestDbFactory.Create();
        var (creator, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var outsider = new User { Email = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.CreateAsync(creator.Id, new CreateCommunityRequestDto("secret", Visibility: CommunityVisibilities.Private));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ListMembersAsync("secret", outsider.Id));

        var asCreator = await service.ListMembersAsync("secret", creator.Id);
        Assert.Single(asCreator.Items);
        Assert.Equal(creator.Id, asCreator.Items[0].UserId);
    }

    private static CommunityService CreateService(KinshoutDbContext db, Mock<ICommunityJoinNotifier>? notifier = null)
    {
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.AnalyzeCommunityAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Community>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string text, IReadOnlyList<Community> communities, CancellationToken _) =>
                Task.FromResult(OpenAiService.FallbackCommunityAnalysis(text, communities)));

        notifier ??= new Mock<ICommunityJoinNotifier>();
        return new CommunityService(db, openAi.Object, notifier.Object);
    }
}
