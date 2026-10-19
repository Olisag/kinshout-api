using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ILikedDiscussionService
{
    Task<DiscussionDto> LikeAsync(Guid userId, Guid discussionId, CancellationToken ct = default);
    Task<DiscussionDto> UnlikeAsync(Guid userId, Guid discussionId, CancellationToken ct = default);
    Task<DiscussionReplyDto> LikeReplyAsync(
        Guid userId,
        Guid discussionId,
        Guid replyId,
        CancellationToken ct = default);
    Task<DiscussionReplyDto> UnlikeReplyAsync(
        Guid userId,
        Guid discussionId,
        Guid replyId,
        CancellationToken ct = default);
    Task<PagedResultDto<DiscussionDto>> ListLikedAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListLikedIdsAsync(Guid userId, CancellationToken ct = default);
}

public class LikedDiscussionService(KinshoutDbContext db) : ILikedDiscussionService
{
    public async Task<DiscussionDto> LikeAsync(Guid userId, Guid discussionId, CancellationToken ct = default)
    {
        var discussion = await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct);
        if (discussion is null)
            throw new KeyNotFoundException("Discussion introuvable.");

        var existing = await db.LikedDiscussions
            .FirstOrDefaultAsync(l => l.UserId == userId && l.DiscussionId == discussionId, ct);
        if (existing is null)
        {
            db.LikedDiscussions.Add(new LikedDiscussion
            {
                UserId = userId,
                DiscussionId = discussionId,
                LikedAt = DateTime.UtcNow,
            });
            discussion.LikeCount++;
            await db.SaveChangesAsync(ct);
        }

        return await LoadDiscussionDtoAsync(discussionId, userId, isLiked: true, ct);
    }

    public async Task<DiscussionDto> UnlikeAsync(Guid userId, Guid discussionId, CancellationToken ct = default)
    {
        var liked = await db.LikedDiscussions
            .FirstOrDefaultAsync(l => l.UserId == userId && l.DiscussionId == discussionId, ct);
        if (liked is not null)
        {
            var discussion = await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct);
            db.LikedDiscussions.Remove(liked);
            if (discussion is not null && discussion.LikeCount > 0)
                discussion.LikeCount--;
            await db.SaveChangesAsync(ct);
        }

        return await LoadDiscussionDtoAsync(discussionId, userId, isLiked: false, ct);
    }

    public async Task<DiscussionReplyDto> LikeReplyAsync(
        Guid userId,
        Guid discussionId,
        Guid replyId,
        CancellationToken ct = default)
    {
        var reply = await db.DiscussionReplies
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == replyId && r.DiscussionId == discussionId, ct);
        if (reply is null)
            throw new KeyNotFoundException("Réponse introuvable.");

        var existing = await db.LikedReplies
            .FirstOrDefaultAsync(l => l.UserId == userId && l.ReplyId == replyId, ct);
        if (existing is null)
        {
            db.LikedReplies.Add(new LikedReply
            {
                UserId = userId,
                ReplyId = replyId,
                LikedAt = DateTime.UtcNow,
            });
            reply.LikeCount++;
            await db.SaveChangesAsync(ct);
        }

        return ToReplyDto(reply, isLiked: true);
    }

    public async Task<DiscussionReplyDto> UnlikeReplyAsync(
        Guid userId,
        Guid discussionId,
        Guid replyId,
        CancellationToken ct = default)
    {
        var reply = await db.DiscussionReplies
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == replyId && r.DiscussionId == discussionId, ct);
        if (reply is null)
            throw new KeyNotFoundException("Réponse introuvable.");

        var liked = await db.LikedReplies
            .FirstOrDefaultAsync(l => l.UserId == userId && l.ReplyId == replyId, ct);
        if (liked is not null)
        {
            db.LikedReplies.Remove(liked);
            if (reply.LikeCount > 0)
                reply.LikeCount--;
            await db.SaveChangesAsync(ct);
        }

        return ToReplyDto(reply, isLiked: false);
    }

    public async Task<PagedResultDto<DiscussionDto>> ListLikedAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var discussionLikes = await db.LikedDiscussions
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .Select(l => new { l.DiscussionId, l.LikedAt })
            .ToListAsync(ct);

        var replyLikes = await db.LikedReplies
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .Select(l => new { DiscussionId = l.Reply.DiscussionId, l.LikedAt })
            .ToListAsync(ct);

        var ranked = discussionLikes
            .Concat(replyLikes)
            .GroupBy(x => x.DiscussionId)
            .Select(g => new { DiscussionId = g.Key, LikedAt = g.Max(x => x.LikedAt) })
            .OrderByDescending(x => x.LikedAt)
            .ToList();

        var total = ranked.Count;
        var pageIds = ranked
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(x => x.DiscussionId)
            .ToList();

        if (pageIds.Count == 0)
            return PagingHelper.Create(Array.Empty<DiscussionDto>(), normalizedPage, normalizedPageSize, total);

        var discussions = await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Community)
            .Where(d => pageIds.Contains(d.Id))
            .ToListAsync(ct);

        var byId = discussions.ToDictionary(d => d.Id);
        var discussionLikedIds = discussionLikes.Select(l => l.DiscussionId).ToHashSet();
        var communityMemberIds = await DiscussionService.LoadViewerApprovedCommunityIdsAsync(
            db, userId, discussions, ct);
        var items = pageIds
            .Where(byId.ContainsKey)
            .Select(id => DiscussionService.ToListDto(
                byId[id],
                isLiked: discussionLikedIds.Contains(id),
                isCommunityMember: DiscussionService.IsCommunityMember(byId[id], communityMemberIds)))
            .ToList();

        return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
    }

    public async Task<IReadOnlyList<Guid>> ListLikedIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var discussionIds = await db.LikedDiscussions
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .Select(l => l.DiscussionId)
            .ToListAsync(ct);

        var replyDiscussionIds = await db.LikedReplies
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .Select(l => l.Reply.DiscussionId)
            .Distinct()
            .ToListAsync(ct);

        return discussionIds.Concat(replyDiscussionIds).Distinct().ToList();
    }

    private async Task<DiscussionDto> LoadDiscussionDtoAsync(
        Guid discussionId,
        Guid userId,
        bool isLiked,
        CancellationToken ct)
    {
        var discussion = await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Community)
            .FirstOrDefaultAsync(d => d.Id == discussionId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        var communityMemberIds = await DiscussionService.LoadViewerApprovedCommunityIdsAsync(
            db, userId, [discussion], ct);
        return DiscussionService.ToListDto(
            discussion,
            isLiked,
            isCommunityMember: DiscussionService.IsCommunityMember(discussion, communityMemberIds));
    }

    private static DiscussionReplyDto ToReplyDto(DiscussionReply reply, bool isLiked) =>
        new(
            reply.Id,
            reply.UserId,
            reply.User.DisplayName,
            TimeHelpers.Initials(reply.User.DisplayName),
            TimeHelpers.FormatRelative(reply.CreatedAt),
            reply.Body,
            reply.ImageUrl,
            reply.VideoUrl,
            ToReplyLocationDto(reply),
            reply.LikeCount,
            isLiked);

    private static DiscussionReplyLocationDto? ToReplyLocationDto(DiscussionReply reply)
    {
        if (reply.Latitude is null || reply.Longitude is null)
            return null;

        return new DiscussionReplyLocationDto(
            reply.Latitude.Value,
            reply.Longitude.Value,
            reply.PlaceName,
            reply.Address);
    }
}
