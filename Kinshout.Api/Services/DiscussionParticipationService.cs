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
        if (discussion.CommunityId is Guid communityId)
            await communities.EnsureJoinedAsync(userId, communityId, ct);

        if (discussion.UserId == userId)
            return;

        var existing = await db.DiscussionParticipants
            .FirstOrDefaultAsync(p => p.DiscussionId == discussionId && p.UserId == userId, ct);

        if (existing?.Status == CommunityMemberStatuses.Approved)
            throw new InvalidOperationException("Vous participez déjà à cette discussion.");

        if (existing?.Status == CommunityMemberStatuses.Pending)
            return;

        if (existing?.Status == CommunityMemberStatuses.Rejected)
            db.DiscussionParticipants.Remove(existing);

        var isPrivate = CommunityVisibilityHelper.IsPrivate(discussion.Visibility);
        var status = isPrivate ? CommunityMemberStatuses.Pending : CommunityMemberStatuses.Approved;

        db.DiscussionParticipants.Add(new DiscussionParticipant
        {
            DiscussionId = discussionId,
            UserId = userId,
            Status = status,
            ReviewedAt = isPrivate ? null : DateTime.UtcNow,
            ReviewedByUserId = isPrivate ? null : userId,
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

        if (participant.Status == CommunityMemberStatuses.Approved)
            return;

        participant.Status = CommunityMemberStatuses.Approved;
        participant.ReviewedAt = DateTime.UtcNow;
        participant.ReviewedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        var member = await db.Users.AsNoTracking().FirstAsync(u => u.Id == targetUserId, ct);
        await joinNotifier.NotifyJoinApprovedAsync(discussion, member, ct);
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

    public async Task EnsureCanViewAsync(Discussion discussion, Guid? viewerUserId, CancellationToken ct = default)
    {
        if (discussion.CommunityId is Guid communityId)
        {
            var community = await db.Communities.AsNoTracking()
                .FirstAsync(c => c.Id == communityId, ct);
            var membership = await FindCommunityMembershipAsync(communityId, viewerUserId, ct);
            if (CommunityAccessHelper.IsApprovedMember(community, membership, viewerUserId))
                return;

            await communities.EnsureCanAccessAsync(communityId, viewerUserId, ct);
        }

        var participant = await FindParticipantAsync(discussion.Id, viewerUserId, ct);
        if (!DiscussionAccessHelper.CanView(discussion, participant, viewerUserId))
            throw new UnauthorizedAccessException(
                "Accès refusé. Rejoignez la discussion ou attendez l'approbation.");
    }

    public async Task EnsureCanParticipateAsync(Discussion discussion, Guid userId, CancellationToken ct = default)
    {
        await EnsureCommunityAccessIfNeededAsync(discussion, userId, ct);

        var participant = await FindParticipantAsync(discussion.Id, userId, ct);
        if (!DiscussionAccessHelper.CanParticipate(discussion, participant, userId))
        {
            if (CommunityVisibilityHelper.IsPrivate(discussion.Visibility))
            {
                var message = discussion.CommunityId is not null
                    ? "Rejoignez cette discussion privée ou attendez l'approbation du créateur ou d'un modérateur."
                    : "Rejoignez cette discussion privée ou attendez l'approbation de l'auteur.";

                throw new UnauthorizedAccessException(message);
            }

            throw new UnauthorizedAccessException("Rejoignez la discussion pour participer.");
        }
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
            isCommunityModerator = CommunityAccessHelper.CanModerate(community, membership, actorUserId);
        }

        if (!DiscussionAccessHelper.CanModerateParticipants(discussion, actorUserId, isCommunityModerator))
        {
            var message = discussion.CommunityId is not null
                ? "Seuls le créateur ou un modérateur de la communauté peuvent gérer les accès."
                : "Seul l'auteur peut gérer les accès à cette discussion.";

            throw new UnauthorizedAccessException(message);
        }
    }

    private async Task EnsureCommunityAccessIfNeededAsync(Discussion discussion, Guid? userId, CancellationToken ct)
    {
        if (discussion.CommunityId is Guid communityId)
            await communities.EnsureCanAccessAsync(communityId, userId, ct);
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
