using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IAccountSuppressionService
{
    /// <summary>
    /// Permanently deletes the signed-in account and every piece of data tied to it
    /// (adverts, discussions, replies, communities they created when empty, likes, uploads, etc.).
    /// </summary>
    Task SuppressAsync(Guid userId, CancellationToken ct = default);
}

public class AccountSuppressionService(
    KinshoutDbContext db,
    IUploadStorage storage,
    ILogger<AccountSuppressionService> logger) : IAccountSuppressionService
{
    public async Task SuppressAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        if (string.Equals(user.Email, ImportSeed.ImportUserEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le compte système ne peut pas être supprimé.");

        logger.LogInformation("Suppressing Kinshout account {UserId} ({Email})", user.Id, user.Email);

        await ClearReviewerReferencesAsync(userId, ct);
        await DeleteVideoAssetsAsync(userId, ct);
        await DeleteAdvertsAsync(userId, ct);
        await DeleteDiscussionsAsync(userId, ct);
        await DeleteRemainingRepliesAsync(userId, ct);
        await TransferOrDeleteOwnedCommunitiesAsync(userId, ct);
        await DeleteStoredAvatarIfOwnedAsync(user, ct);

        // Memberships, logins, likes, saved adverts, discussion participants cascade from the user row
        // where the FK is Cascade; remove them explicitly so InMemory tests stay consistent.
        db.CommunityMembers.RemoveRange(
            await db.CommunityMembers.Where(m => m.UserId == userId).ToListAsync(ct));
        db.DiscussionParticipants.RemoveRange(
            await db.DiscussionParticipants.Where(p => p.UserId == userId).ToListAsync(ct));
        db.SavedAdverts.RemoveRange(
            await db.SavedAdverts.Where(s => s.UserId == userId).ToListAsync(ct));
        db.LikedDiscussions.RemoveRange(
            await db.LikedDiscussions.Where(l => l.UserId == userId).ToListAsync(ct));
        db.LikedReplies.RemoveRange(
            await db.LikedReplies.Where(l => l.UserId == userId).ToListAsync(ct));
        db.UserLogins.RemoveRange(
            await db.UserLogins.Where(l => l.UserId == userId).ToListAsync(ct));

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Suppressed Kinshout account {UserId}", userId);
    }

    private async Task ClearReviewerReferencesAsync(Guid userId, CancellationToken ct)
    {
        var communityReviews = await db.CommunityMembers
            .Where(m => m.ReviewedByUserId == userId)
            .ToListAsync(ct);
        foreach (var membership in communityReviews)
            membership.ReviewedByUserId = null;

        var discussionReviews = await db.DiscussionParticipants
            .Where(p => p.ReviewedByUserId == userId)
            .ToListAsync(ct);
        foreach (var participant in discussionReviews)
            participant.ReviewedByUserId = null;

        if (communityReviews.Count > 0 || discussionReviews.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task DeleteVideoAssetsAsync(Guid userId, CancellationToken ct)
    {
        var assets = await db.VideoAssets.Where(v => v.UserId == userId).ToListAsync(ct);
        foreach (var asset in assets)
        {
            await TryDeleteUploadAsync(asset.VideoUrl, ct);
            await TryDeleteUploadAsync(asset.PosterUrl, ct);
            await TryDeleteUploadAsync(asset.SourceUrl, ct);
        }

        if (assets.Count > 0)
        {
            db.VideoAssets.RemoveRange(assets);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task DeleteAdvertsAsync(Guid userId, CancellationToken ct)
    {
        var adverts = await db.Adverts.Where(a => a.UserId == userId).ToListAsync(ct);
        foreach (var advert in adverts)
            await DeleteAdvertUploadsAsync(advert, ct);

        if (adverts.Count == 0)
            return;

        var advertIds = adverts.Select(a => a.Id).ToList();
        db.SavedAdverts.RemoveRange(
            await db.SavedAdverts.Where(s => advertIds.Contains(s.AdvertId)).ToListAsync(ct));
        db.Adverts.RemoveRange(adverts);
        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteDiscussionsAsync(Guid userId, CancellationToken ct)
    {
        var discussions = await db.Discussions.Where(d => d.UserId == userId).ToListAsync(ct);
        foreach (var discussion in discussions)
        {
            await DeleteDiscussionUploadsAsync(discussion, ct);

            var replies = await db.DiscussionReplies
                .Where(r => r.DiscussionId == discussion.Id)
                .ToListAsync(ct);
            foreach (var reply in replies)
                await DeleteReplyUploadsAsync(reply, ct);

            var replyIds = replies.Select(r => r.Id).ToList();
            if (replyIds.Count > 0)
            {
                db.LikedReplies.RemoveRange(
                    await db.LikedReplies.Where(l => replyIds.Contains(l.ReplyId)).ToListAsync(ct));
                db.DiscussionReplies.RemoveRange(replies);
            }

            db.LikedDiscussions.RemoveRange(
                await db.LikedDiscussions.Where(l => l.DiscussionId == discussion.Id).ToListAsync(ct));
            db.DiscussionParticipants.RemoveRange(
                await db.DiscussionParticipants.Where(p => p.DiscussionId == discussion.Id).ToListAsync(ct));
        }

        if (discussions.Count > 0)
        {
            db.Discussions.RemoveRange(discussions);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task DeleteRemainingRepliesAsync(Guid userId, CancellationToken ct)
    {
        var replies = await db.DiscussionReplies
            .Where(r => r.UserId == userId)
            .ToListAsync(ct);
        if (replies.Count == 0)
            return;

        foreach (var reply in replies)
            await DeleteReplyUploadsAsync(reply, ct);

        var replyIds = replies.Select(r => r.Id).ToList();
        db.LikedReplies.RemoveRange(
            await db.LikedReplies.Where(l => replyIds.Contains(l.ReplyId)).ToListAsync(ct));

        var discussionIds = replies.Select(r => r.DiscussionId).Distinct().ToList();
        var discussions = await db.Discussions
            .Where(d => discussionIds.Contains(d.Id))
            .ToListAsync(ct);
        var removedByDiscussion = replies.GroupBy(r => r.DiscussionId)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var discussion in discussions)
        {
            if (removedByDiscussion.TryGetValue(discussion.Id, out var removed))
                discussion.ReplyCount = Math.Max(0, discussion.ReplyCount - removed);
        }

        db.DiscussionReplies.RemoveRange(replies);
        await db.SaveChangesAsync(ct);
    }

    private async Task TransferOrDeleteOwnedCommunitiesAsync(Guid userId, CancellationToken ct)
    {
        var communities = await db.Communities
            .Where(c => c.CreatedByUserId == userId)
            .ToListAsync(ct);
        if (communities.Count == 0)
            return;

        User? importUser = null;
        foreach (var community in communities)
        {
            if (community.Slug == CommunityDefaults.GeneralSlug)
            {
                importUser ??= await ImportSeed.EnsureImportUserAsync(db, ct);
                community.CreatedByUserId = importUser.Id;
                continue;
            }

            var hasDiscussions = await db.Discussions.AnyAsync(d => d.CommunityId == community.Id, ct);
            if (!hasDiscussions)
            {
                db.CommunityMembers.RemoveRange(
                    await db.CommunityMembers.Where(m => m.CommunityId == community.Id).ToListAsync(ct));
                db.Communities.Remove(community);
                continue;
            }

            importUser ??= await ImportSeed.EnsureImportUserAsync(db, ct);
            community.CreatedByUserId = importUser.Id;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteStoredAvatarIfOwnedAsync(User user, CancellationToken ct)
    {
        var avatarUrl = user.AvatarUrl;
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return;

        var ownedPrefix = $"/uploads/avatars/{user.Id:N}/";
        if (!avatarUrl.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase)
            && !avatarUrl.Contains(ownedPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        await TryDeleteUploadAsync(avatarUrl, ct);
    }

    private async Task DeleteAdvertUploadsAsync(Advert advert, CancellationToken ct)
    {
        var imageUrls = JsonSerializer.Deserialize<List<string>>(advert.ImageUrlsJson ?? "[]") ?? [];
        foreach (var url in imageUrls)
        {
            await TryDeleteUploadAsync(url, ct);
            await TryDeleteUploadAsync(AdvertImageUrls.GetThumbnailPath(url), ct);
            await TryDeleteUploadAsync(AdvertImageUrls.GetDisplayPath(url), ct);
        }

        if (!string.IsNullOrWhiteSpace(advert.ResumeUrl))
            await TryDeleteUploadAsync(advert.ResumeUrl, ct);
    }

    private async Task DeleteDiscussionUploadsAsync(Discussion discussion, CancellationToken ct)
    {
        foreach (var url in DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson))
            await TryDeleteUploadAsync(url, ct);
        foreach (var url in DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson))
            await TryDeleteUploadAsync(url, ct);
    }

    private async Task DeleteReplyUploadsAsync(DiscussionReply reply, CancellationToken ct)
    {
        await TryDeleteUploadAsync(reply.ImageUrl, ct);
        await TryDeleteUploadAsync(reply.VideoUrl, ct);
    }

    private async Task TryDeleteUploadAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            if (url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/uploads/", StringComparison.OrdinalIgnoreCase))
                await storage.DeleteIfExistsAsync(url, ct);
        }
        catch (ArgumentException)
        {
            // Ignore invalid or external URLs.
        }
    }
}
