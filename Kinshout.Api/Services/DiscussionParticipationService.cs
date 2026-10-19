using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IDiscussionParticipationService
{
    Task RequestJoinAsync(Guid userId, Guid discussionId, CancellationToken ct = default);
    Task ApproveParticipantAsync(Guid actorUserId, Guid discussionId, Guid targetUserId, CancellationToken ct = default);
    Task RejectParticipantAsync(Guid actorUserId, Guid discussionId, Guid targetUserId, CancellationToken ct = default);
    Task<PagedResultDto<DiscussionParticipantDto>> ListPendingParticipantsAsync(
        Guid actorUserId,
        Guid discussionId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task EnsureCanViewAsync(Discussion discussion, Guid? viewerUserId, CancellationToken ct = default);
    Task EnsureCanParticipateAsync(Discussion discussion, Guid userId, CancellationToken ct = default);
    Task SeedAuthorParticipantAsync(Discussion discussion, CancellationToken ct = default);
}

public class DiscussionParticipationService(
    KinshoutDbContext db,
    ICommunityService communities,
    IDiscussionJoinNotifier joinNotifier) : IDiscussionParticipationService
{
    public async Task RequestJoinAsync(Guid userId, Guid discussionId, CancellationToken ct = default)
    {
        var discussion = await RequireDiscussionAsync(discussionId, ct);
        var isPrivate = CommunityVisibilityHelper.IsPrivate(discussion.Visibility);
        var needsApproval = isPrivate;

        if (discussion.CommunityId is Guid communityId)
        {
            var community = await db.Communities.AsNoTracking().FirstAsync(c => c.Id == communityId, ct);

            // Public discussion in a public community grants community access immediately.
            // Otherwise the join only requests community membership until approved.
            if (isPrivate || CommunityVisibilityHelper.IsPrivate(community.Visibility))
            {
                await communities.EnsureJoinedAsync(userId, communityId, ct);
                var membership = await FindCommunityMembershipAsync(communityId, userId, ct);
                needsApproval = isPrivate || !CommunityAccessHelper.IsApprovedMember(community, membership, userId);
            }
            else
            {
                await communities.EnsureApprovedMemberAsync(userId, communityId, userId, ct);
            }
        }

        if (discussion.UserId == userId)
            return;

        var existing = await db.DiscussionParticipants
            .FirstOrDefaultAsync(p => p.DiscussionId == discussionId && p.UserId == userId, ct);

        if (existing?.Status == CommunityMemberStatuses.Approved)
            throw new InvalidOperationException("Vous participez déjà à cette discussion.");

        if (existing?.Status == CommunityMemberStatuses.Pending)
        {
            if (needsApproval)
                return;

            existing.Status = CommunityMemberStatuses.Approved;
            existing.ReviewedAt = DateTime.UtcNow;
            existing.ReviewedByUserId = userId;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (existing?.Status == CommunityMemberStatuses.Rejected)
            db.DiscussionParticipants.Remove(existing);

        db.DiscussionParticipants.Add(new DiscussionParticipant
        {
            DiscussionId = discussionId,
            UserId = userId,
            Status = needsApproval ? CommunityMemberStatuses.Pending : CommunityMemberStatuses.Approved,
            ReviewedAt = needsApproval ? null : DateTime.UtcNow,
            ReviewedByUserId = needsApproval ? null : userId,
        });
        await db.SaveChangesAsync(ct);

        if (isPrivate)
        {
            var requester = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            await joinNotifier.NotifyJoinRequestAsync(discussion, requester, ct);
        }
    }

    public async Task ApproveParticipantAsync(
        Guid actorUserId,
        Guid discussionId,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var discussion = await RequireDiscussionAsync(discussionId, ct);
        await EnsureCanModerateAsync(discussion, actorUserId, ct);

        var participant = await db.DiscussionParticipants
            .FirstOrDefaultAsync(p => p.DiscussionId == discussionId && p.UserId == targetUserId, ct)
            ?? throw new KeyNotFoundException("Demande d'accès introuvable.");

        var wasPending = participant.Status != CommunityMemberStatuses.Approved;
        if (wasPending)
        {
            participant.Status = CommunityMemberStatuses.Approved;
            participant.ReviewedAt = DateTime.UtcNow;
            participant.ReviewedByUserId = actorUserId;
            await db.SaveChangesAsync(ct);
        }

        // Discussion approval grants full community membership.
        if (discussion.CommunityId is Guid communityId)
            await communities.EnsureApprovedMemberAsync(targetUserId, communityId, actorUserId, ct);

        if (wasPending)
        {
            var member = await db.Users.AsNoTracking().FirstAsync(u => u.Id == targetUserId, ct);
            await joinNotifier.NotifyJoinApprovedAsync(discussion, member, ct);
        }
    }

    public async Task RejectParticipantAsync(
        Guid actorUserId,
        Guid discussionId,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var discussion = await RequireDiscussionAsync(discussionId, ct);
        await EnsureCanModerateAsync(discussion, actorUserId, ct);

        var participant = await db.DiscussionParticipants
            .FirstOrDefaultAsync(p => p.DiscussionId == discussionId && p.UserId == targetUserId, ct)
            ?? throw new KeyNotFoundException("Demande d'accès introuvable.");

        participant.Status = CommunityMemberStatuses.Rejected;
        participant.ReviewedAt = DateTime.UtcNow;
        participant.ReviewedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        var member = await db.Users.AsNoTracking().FirstAsync(u => u.Id == targetUserId, ct);
        await joinNotifier.NotifyJoinRejectedAsync(discussion, member, ct);
    }

    public async Task<PagedResultDto<DiscussionParticipantDto>> ListPendingParticipantsAsync(
        Guid actorUserId,
        Guid discussionId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var discussion = await RequireDiscussionAsync(discussionId, ct);
        await EnsureCanModerateAsync(discussion, actorUserId, ct);

        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var query = db.DiscussionParticipants
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.DiscussionId == discussionId && p.Status == CommunityMemberStatuses.Pending)
            .OrderBy(p => p.CreatedAt);

        var total = await query.CountAsync(ct);
        var rows = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(p => new DiscussionParticipantDto(p.UserId, p.User.DisplayName, p.Status, p.CreatedAt))
            .ToList();

        return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
    }

    /// <summary>
    /// Public discussions are readable by anyone, including those in private communities
    /// (they are only hidden from feeds). Private discussions require an approved community
    /// member or participant.
    /// </summary>
    public async Task EnsureCanViewAsync(Discussion discussion, Guid? viewerUserId, CancellationToken ct = default)
    {
        var (community, membership) = await LoadCommunityAccessAsync(discussion, viewerUserId, ct);
        var isApprovedMember = community is not null
            && CommunityAccessHelper.CanViewPrivateDiscussions(community, membership, viewerUserId);

        var participant = await FindParticipantAsync(discussion.Id, viewerUserId, ct);
        if (DiscussionAccessHelper.CanView(discussion, participant, viewerUserId, isApprovedMember))
            return;

        var communityRequired = community is not null
            && !CommunityAccessHelper.CanViewDiscussions(community, membership, viewerUserId);
        throw new DiscussionAccessDeniedException(DiscussionJoinPrompt.Build(
            discussion,
            community,
            viewerUserId,
            communityRequired,
            communityRequired ? membership?.Status ?? participant?.Status : participant?.Status,
            toRead: true));
    }

    public async Task EnsureCanParticipateAsync(Discussion discussion, Guid userId, CancellationToken ct = default)
    {
        var (community, membership) = await LoadCommunityAccessAsync(discussion, userId, ct);
        var communityRequired = community is not null
            && !CommunityAccessHelper.CanViewDiscussions(community, membership, userId);

        var participant = await FindParticipantAsync(discussion.Id, userId, ct);
        if (!communityRequired && DiscussionAccessHelper.CanParticipate(discussion, participant, userId))
            return;

        throw new DiscussionAccessDeniedException(DiscussionJoinPrompt.Build(
            discussion,
            community,
            userId,
            communityRequired,
            communityRequired ? membership?.Status ?? participant?.Status : participant?.Status,
            toRead: false));
    }

    private async Task<(Community? Community, CommunityMember? Membership)> LoadCommunityAccessAsync(
        Discussion discussion,
        Guid? userId,
        CancellationToken ct)
    {
        if (discussion.CommunityId is not Guid communityId)
            return (null, null);

        var community = await db.Communities.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new KeyNotFoundException("Communauté introuvable.");
        return (community, await FindCommunityMembershipAsync(communityId, userId, ct));
    }

    public async Task SeedAuthorParticipantAsync(Discussion discussion, CancellationToken ct)
    {
        if (await db.DiscussionParticipants.AnyAsync(
                p => p.DiscussionId == discussion.Id && p.UserId == discussion.UserId, ct))
            return;

        db.DiscussionParticipants.Add(new DiscussionParticipant
        {
            DiscussionId = discussion.Id,
            UserId = discussion.UserId,
            Status = CommunityMemberStatuses.Approved,
            ReviewedAt = DateTime.UtcNow,
            ReviewedByUserId = discussion.UserId,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureCanModerateAsync(Discussion discussion, Guid actorUserId, CancellationToken ct)
    {
        var isCommunityModerator = false;
        if (discussion.CommunityId is Guid communityId)
        {
            var community = await db.Communities.AsNoTracking().FirstAsync(c => c.Id == communityId, ct);
            var membership = await db.CommunityMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorUserId, ct);
            isCommunityModerator = CommunityAccessHelper.CanModerate(community, membership, actorUserId)
                || (CommunityAccessHelper.IsGeneral(community) && DiscussionAccessHelper.IsAuthor(discussion, actorUserId));
        }

        if (!DiscussionAccessHelper.CanModerateParticipants(discussion, actorUserId, isCommunityModerator))
        {
            var message = discussion.CommunityId is not null
                ? "Seuls le créateur ou un modérateur de la communauté peuvent gérer les accès."
                : "Seul l'auteur peut gérer les accès à cette discussion.";

            throw new UnauthorizedAccessException(message);
        }
    }

    private async Task<Discussion> RequireDiscussionAsync(Guid discussionId, CancellationToken ct) =>
        await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct)
        ?? throw new KeyNotFoundException("Discussion introuvable.");

    private async Task<DiscussionParticipant?> FindParticipantAsync(
        Guid discussionId,
        Guid? userId,
        CancellationToken ct)
    {
        if (userId is null)
            return null;

        return await db.DiscussionParticipants
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.DiscussionId == discussionId && p.UserId == userId, ct);
    }

    private async Task<CommunityMember?> FindCommunityMembershipAsync(
        Guid communityId,
        Guid? userId,
        CancellationToken ct)
    {
        if (userId is null)
            return null;

        return await db.CommunityMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == userId, ct);
    }
}
