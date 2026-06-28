using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ILikedDiscussionService
{
    Task<DiscussionDto> LikeAsync(Guid userId, Guid discussionId, CancellationToken ct = default);
    Task<DiscussionDto> UnlikeAsync(Guid userId, Guid discussionId, CancellationToken ct = default);
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

        return await LoadDiscussionDtoAsync(discussionId, isLiked: true, ct);
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

        return await LoadDiscussionDtoAsync(discussionId, isLiked: false, ct);
    }

    public async Task<IReadOnlyList<Guid>> ListLikedIdsAsync(Guid userId, CancellationToken ct = default) =>
        await db.LikedDiscussions
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .Select(l => l.DiscussionId)
            .ToListAsync(ct);

    private async Task<DiscussionDto> LoadDiscussionDtoAsync(Guid discussionId, bool isLiked, CancellationToken ct)
    {
        var discussion = await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .FirstOrDefaultAsync(d => d.Id == discussionId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        return DiscussionService.ToListDto(discussion, isLiked);
    }
}
