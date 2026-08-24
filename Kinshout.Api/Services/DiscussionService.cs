using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

public interface IDiscussionService
{
    Task<PagedResultDto<DiscussionDto>> ListAsync(
        string? query = null,
        Guid? categoryId = null,
        Guid? communityId = null,
        string? communitySlug = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        Guid? viewerUserId = null,
        CancellationToken ct = default);
    Task<PagedResultDto<DiscussionDto>> ListMineAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string filter = DiscussionMineFilterHelper.All,
        CancellationToken ct = default);
    Task<DiscussionDetailDto?> GetByIdAsync(
        Guid id,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        Guid? viewerUserId = null,
        CancellationToken ct = default);
    Task<DiscussionDto> CreateAsync(Guid userId, CreateDiscussionRequestDto request, CancellationToken ct = default);
    Task<DiscussionDetailDto> UpdateAsync(Guid userId, Guid discussionId, UpdateDiscussionRequestDto request, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid discussionId, CancellationToken ct = default);
    Task<DiscussionDto> AddMediaAsync(Guid userId, Guid discussionId, DiscussionMediaUpdateRequestDto request, CancellationToken ct = default);
    Task<DiscussionDto> RemoveMediaAsync(Guid userId, Guid discussionId, DiscussionMediaUpdateRequestDto request, CancellationToken ct = default);
    Task<DiscussionReplyDto> AddReplyAsync(Guid userId, Guid discussionId, CreateReplyRequestDto request, CancellationToken ct = default);
    Task<DiscussionReplyDto> UpdateReplyAsync(
        Guid userId,
        Guid discussionId,
        Guid replyId,
        UpdateReplyRequestDto request,
        CancellationToken ct = default);
    Task DeleteReplyAsync(Guid userId, Guid discussionId, Guid replyId, CancellationToken ct = default);
}

public class DiscussionService(
    KinshoutDbContext db,
    IOpenAiService openAi,
    IAdvertModerationService moderation,
    IUploadStorage storage,
    IMemoryCache cache) : IDiscussionService
{
    public async Task<PagedResultDto<DiscussionDto>> ListAsync(
        string? query = null,
        Guid? categoryId = null,
        Guid? communityId = null,
        string? communitySlug = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        Guid? viewerUserId = null,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var q = db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Community)
            .AsQueryable();

        if (categoryId is not null)
            q = q.Where(d => d.CategoryId == categoryId);

        if (communityId is not null)
            q = q.Where(d => d.CommunityId == communityId);

        if (!string.IsNullOrWhiteSpace(communitySlug))
        {
            var slug = CommunitySlugHelper.Normalize(communitySlug);
            q = q.Where(d => d.Community != null && d.Community.Slug == slug);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var lower = query.ToLowerInvariant();
            q = q.Where(d => d.Title.ToLower().Contains(lower) || d.Body.ToLower().Contains(lower));
        }

        var ordered = ListSortHelper.IsPopular(sort)
            ? DiscussionSourceMapper.OrderByPopular(q)
            : q.OrderByDescending(d => d.CreatedAt);

        var total = await ordered.CountAsync(ct);
        var items = await ordered
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var likedIds = await LoadLikedDiscussionIdsAsync(db, viewerUserId, items.Select(d => d.Id), ct);
        return PagingHelper.Create(
            items.Select(d => ToListDto(d, likedIds.Contains(d.Id))).ToList(),
            normalizedPage,
            normalizedPageSize,
            total);
    }

    public async Task<PagedResultDto<DiscussionDto>> ListMineAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string filter = DiscussionMineFilterHelper.All,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var normalizedFilter = DiscussionMineFilterHelper.Normalize(filter);

        var query = db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Community)
            .AsQueryable();

        query = normalizedFilter switch
        {
            DiscussionMineFilterHelper.Authored => query.Where(d => d.UserId == userId),
            DiscussionMineFilterHelper.Replies => query.Where(d =>
                d.UserId != userId && d.Replies.Any(r => r.UserId == userId)),
            _ => query.Where(d => d.UserId == userId || d.Replies.Any(r => r.UserId == userId)),
        };

        query = normalizedFilter switch
        {
            DiscussionMineFilterHelper.Authored => query.OrderByDescending(d => d.UpdatedAt),
            DiscussionMineFilterHelper.Replies => query.OrderByDescending(d =>
                d.Replies.Where(r => r.UserId == userId).Select(r => (DateTime?)r.CreatedAt).Max()),
            _ => query.OrderByDescending(d =>
                d.Replies.Where(r => r.UserId == userId).Select(r => (DateTime?)r.CreatedAt).Max()
                ?? d.UpdatedAt),
        };

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var likedIds = await LoadLikedDiscussionIdsAsync(db, userId, items.Select(d => d.Id), ct);
        return PagingHelper.Create(
            items.Select(d => ToListDto(d, likedIds.Contains(d.Id))).ToList(),
            normalizedPage,
            normalizedPageSize,
            total);
    }

    public async Task<DiscussionDetailDto?> GetByIdAsync(
        Guid id,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        Guid? viewerUserId = null,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var d = await db.Discussions
            .AsNoTracking()
            .Include(x => x.User)
            .Include(x => x.Community)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (d is null)
            return null;

        var viewCount = await IncrementViewCountAsync(id, d.ViewCount, ct);
        var isLiked = await IsLikedByUserAsync(db, viewerUserId, id, ct);

        var repliesQuery = db.DiscussionReplies
            .AsNoTracking()
            .Include(r => r.User)
            .Where(r => r.DiscussionId == id)
            .OrderBy(r => r.CreatedAt);

        var total = await repliesQuery.CountAsync(ct);
        var replies = await repliesQuery
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var thread = PagingHelper.Create(
            replies.Select(ToReplyDto).ToList(),
            normalizedPage,
            normalizedPageSize,
            total);

        var images = DiscussionMediaHelper.ParseUrlList(d.ImageUrlsJson);
        var videos = DiscussionMediaHelper.ParseUrlList(d.VideoUrlsJson);

        return new DiscussionDetailDto(
            d.Id,
            d.Title,
            d.Body,
            d.UserId,
            d.User.DisplayName,
            TimeHelpers.Initials(d.User.DisplayName),
            TimeHelpers.FormatRelative(DiscussionSourceMapper.SortDate(d)),
            d.LikeCount,
            viewCount,
            d.ReplyCount,
            isLiked,
            thread,
            d.IsExternal,
            DiscussionSourceMapper.ToSourceDto(d),
            d.Community is null ? null : CommunitySlugHelper.ToRouteSlug(d.Community.Slug),
            DiscussionMediaHelper.ToMediaDtos(images, videos));
    }

    public async Task<DiscussionDetailDto> UpdateAsync(
        Guid userId,
        Guid discussionId,
        UpdateDiscussionRequestDto request,
        CancellationToken ct = default)
    {
        var title = request.Title.Trim();
        var body = request.Body.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Le titre et le message sont requis.");

        var discussion = await db.Discussions
            .FirstOrDefaultAsync(d => d.Id == discussionId && d.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        await moderation.EnsureTextAllowedAsync($"{title}\n{body}", ct);

        var category = await AssignTopicCategoryAsync($"{title}. {body}", ct);
        Guid? communityId = discussion.CommunityId;
        if (request.CommunitySlug is not null)
        {
            communityId = string.IsNullOrWhiteSpace(request.CommunitySlug)
                ? null
                : await ResolveCommunityIdAsync(request.CommunitySlug, ct);
        }

        var images = request.ImageUrls is null
            ? DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson)
            : DiscussionMediaHelper.NormalizeUrls(
                request.ImageUrls, userId, "images", DiscussionMediaHelper.MaxImages, "photos");
        var videos = request.VideoUrls is null
            ? DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson)
            : DiscussionMediaHelper.NormalizeUrls(
                request.VideoUrls, userId, "videos", DiscussionMediaHelper.MaxVideos, "vidéos");

        discussion.Title = title;
        discussion.Body = body;
        discussion.CategoryId = category.Id;
        discussion.TopicSlug = category.Slug;
        discussion.CommunityId = communityId;
        discussion.ImageUrlsJson = DiscussionMediaHelper.SerializeUrlList(images);
        discussion.VideoUrlsJson = DiscussionMediaHelper.SerializeUrlList(videos);
        discussion.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return (await GetByIdAsync(discussionId, viewerUserId: userId, ct: ct))!;
    }

    public async Task DeleteAsync(Guid userId, Guid discussionId, CancellationToken ct = default)
    {
        var discussion = await db.Discussions
            .FirstOrDefaultAsync(d => d.Id == discussionId && d.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        await DeleteStoredMediaAsync(discussion, ct);
        db.Discussions.Remove(discussion);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DiscussionDto> CreateAsync(Guid userId, CreateDiscussionRequestDto request, CancellationToken ct = default)
    {
        await moderation.EnsureTextAllowedAsync($"{request.Title}\n{request.Body}", ct);

        var category = await AssignTopicCategoryAsync($"{request.Title}. {request.Body}", ct);
        var communityId = await ResolveCommunityIdAsync(request.CommunitySlug, ct);
        var images = DiscussionMediaHelper.NormalizeUrls(
            request.ImageUrls, userId, "images", DiscussionMediaHelper.MaxImages, "photos");
        var videos = DiscussionMediaHelper.NormalizeUrls(
            request.VideoUrls, userId, "videos", DiscussionMediaHelper.MaxVideos, "vidéos");

        var discussion = new Discussion
        {
            UserId = userId,
            CategoryId = category.Id,
            TopicSlug = category.Slug,
            CommunityId = communityId,
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            ImageUrlsJson = DiscussionMediaHelper.SerializeUrlList(images),
            VideoUrlsJson = DiscussionMediaHelper.SerializeUrlList(videos),
        };

        db.Discussions.Add(discussion);
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        discussion.User = user;
        discussion.Category = category;
        if (communityId is not null)
            discussion.Community = await db.Communities.AsNoTracking().FirstAsync(c => c.Id == communityId, ct);
        discussion.Replies = [];
        return ToListDto(discussion, isLiked: false);
    }

    public async Task<DiscussionDto> AddMediaAsync(
        Guid userId,
        Guid discussionId,
        DiscussionMediaUpdateRequestDto request,
        CancellationToken ct = default)
    {
        var discussion = await db.Discussions
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Community)
            .FirstOrDefaultAsync(d => d.Id == discussionId && d.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        var images = DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson);
        var videos = DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson);

        var addImages = DiscussionMediaHelper.NormalizeUrls(
            request.ImageUrls, userId, "images", DiscussionMediaHelper.MaxImages, "photos");
        var addVideos = DiscussionMediaHelper.NormalizeUrls(
            request.VideoUrls, userId, "videos", DiscussionMediaHelper.MaxVideos, "vidéos");

        foreach (var url in addImages.Where(url => !images.Contains(url, StringComparer.OrdinalIgnoreCase)))
            images.Add(url);
        foreach (var url in addVideos.Where(url => !videos.Contains(url, StringComparer.OrdinalIgnoreCase)))
            videos.Add(url);

        if (images.Count > DiscussionMediaHelper.MaxImages)
            throw new ArgumentException($"Maximum {DiscussionMediaHelper.MaxImages} photos par discussion.");
        if (videos.Count > DiscussionMediaHelper.MaxVideos)
            throw new ArgumentException($"Maximum {DiscussionMediaHelper.MaxVideos} vidéos par discussion.");

        discussion.ImageUrlsJson = DiscussionMediaHelper.SerializeUrlList(images);
        discussion.VideoUrlsJson = DiscussionMediaHelper.SerializeUrlList(videos);
        discussion.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToListDto(discussion, isLiked: false);
    }

    public async Task<DiscussionDto> RemoveMediaAsync(
        Guid userId,
        Guid discussionId,
        DiscussionMediaUpdateRequestDto request,
        CancellationToken ct = default)
    {
        var discussion = await db.Discussions
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Community)
            .FirstOrDefaultAsync(d => d.Id == discussionId && d.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        var removeUrls = (request.Urls ?? [])
            .Concat(request.ImageUrls ?? [])
            .Concat(request.VideoUrls ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removeUrls.Count == 0)
            throw new ArgumentException("Aucune URL média à retirer.");

        var images = DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson)
            .Where(url => !removeUrls.Contains(url))
            .ToList();
        var videos = DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson)
            .Where(url => !removeUrls.Contains(url))
            .ToList();

        foreach (var url in removeUrls)
            await TryDeleteUploadAsync(url, ct);

        discussion.ImageUrlsJson = DiscussionMediaHelper.SerializeUrlList(images);
        discussion.VideoUrlsJson = DiscussionMediaHelper.SerializeUrlList(videos);
        discussion.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToListDto(discussion, isLiked: false);
    }

    public async Task<DiscussionReplyDto> AddReplyAsync(
        Guid userId,
        Guid discussionId,
        CreateReplyRequestDto request,
        CancellationToken ct = default)
    {
        var discussion = await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        await moderation.EnsureTextAllowedAsync(request.Body, ct);

        var attachment = ResolveReplyAttachment(userId, request.ImageUrl, request.VideoUrl, request.Location);

        var reply = new DiscussionReply
        {
            DiscussionId = discussionId,
            UserId = userId,
            Body = request.Body.Trim(),
            ImageUrl = attachment.ImageUrl,
            VideoUrl = attachment.VideoUrl,
            Latitude = attachment.Latitude,
            Longitude = attachment.Longitude,
            PlaceName = attachment.PlaceName,
            Address = attachment.Address,
        };

        db.DiscussionReplies.Add(reply);
        discussion.ReplyCount++;
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        return ToReplyDto(reply, user);
    }

    public async Task<DiscussionReplyDto> UpdateReplyAsync(
        Guid userId,
        Guid discussionId,
        Guid replyId,
        UpdateReplyRequestDto request,
        CancellationToken ct = default)
    {
        var body = request.Body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Le message est requis.");

        var reply = await db.DiscussionReplies
            .FirstOrDefaultAsync(r => r.Id == replyId && r.DiscussionId == discussionId && r.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Réponse introuvable.");

        await moderation.EnsureTextAllowedAsync(body, ct);

        var attachment = ResolveReplyAttachment(userId, request.ImageUrl, request.VideoUrl, request.Location);

        reply.Body = body;
        reply.ImageUrl = attachment.ImageUrl;
        reply.VideoUrl = attachment.VideoUrl;
        reply.Latitude = attachment.Latitude;
        reply.Longitude = attachment.Longitude;
        reply.PlaceName = attachment.PlaceName;
        reply.Address = attachment.Address;
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        return ToReplyDto(reply, user);
    }

    public async Task DeleteReplyAsync(Guid userId, Guid discussionId, Guid replyId, CancellationToken ct = default)
    {
        var reply = await db.DiscussionReplies
            .FirstOrDefaultAsync(r => r.Id == replyId && r.DiscussionId == discussionId && r.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Réponse introuvable.");

        var discussion = await db.Discussions.FirstOrDefaultAsync(d => d.Id == discussionId, ct)
            ?? throw new KeyNotFoundException("Discussion introuvable.");

        db.DiscussionReplies.Remove(reply);
        if (discussion.ReplyCount > 0)
            discussion.ReplyCount--;
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> IncrementViewCountAsync(Guid id, int currentCount, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Discussions.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (tracked is null)
                return currentCount;

            tracked.ViewCount++;
            await db.SaveChangesAsync(ct);
            return tracked.ViewCount;
        }

        await db.Discussions
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ViewCount, d => d.ViewCount + 1), ct);

        return currentCount + 1;
    }

    internal static async Task<HashSet<Guid>> LoadLikedDiscussionIdsAsync(
        KinshoutDbContext db,
        Guid? viewerUserId,
        IEnumerable<Guid> discussionIds,
        CancellationToken ct)
    {
        if (viewerUserId is null)
            return [];

        var ids = discussionIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var liked = await db.LikedDiscussions
            .AsNoTracking()
            .Where(l => l.UserId == viewerUserId.Value && ids.Contains(l.DiscussionId))
            .Select(l => l.DiscussionId)
            .ToListAsync(ct);

        return liked.ToHashSet();
    }

    internal static async Task<bool> IsLikedByUserAsync(
        KinshoutDbContext db,
        Guid? viewerUserId,
        Guid discussionId,
        CancellationToken ct)
    {
        if (viewerUserId is null)
            return false;

        return await db.LikedDiscussions
            .AsNoTracking()
            .AnyAsync(l => l.UserId == viewerUserId.Value && l.DiscussionId == discussionId, ct);
    }

    private static DiscussionReplyDto ToReplyDto(DiscussionReply reply) =>
        new(
            reply.Id,
            reply.UserId,
            reply.User.DisplayName,
            TimeHelpers.Initials(reply.User.DisplayName),
            TimeHelpers.FormatRelative(reply.CreatedAt),
            reply.Body,
            reply.ImageUrl,
            reply.VideoUrl,
            ToReplyLocationDto(reply));

    private static DiscussionReplyDto ToReplyDto(DiscussionReply reply, User user) =>
        new(
            reply.Id,
            reply.UserId,
            user.DisplayName,
            TimeHelpers.Initials(user.DisplayName),
            TimeHelpers.FormatRelative(reply.CreatedAt),
            reply.Body,
            reply.ImageUrl,
            reply.VideoUrl,
            ToReplyLocationDto(reply));

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

    private sealed record ReplyAttachment(
        string? ImageUrl,
        string? VideoUrl,
        double? Latitude,
        double? Longitude,
        string? PlaceName,
        string? Address);

    private static ReplyAttachment ResolveReplyAttachment(
        Guid userId,
        string? imageUrl,
        string? videoUrl,
        DiscussionReplyLocationDto? location)
    {
        var hasImage = !string.IsNullOrWhiteSpace(imageUrl);
        var hasVideo = !string.IsNullOrWhiteSpace(videoUrl);
        var hasLocation = location is not null;
        var count = (hasImage ? 1 : 0) + (hasVideo ? 1 : 0) + (hasLocation ? 1 : 0);

        if (count > 1)
        {
            throw new ArgumentException(
                "Une réponse ne peut avoir qu'une seule pièce jointe (photo, vidéo ou lieu).");
        }

        if (hasImage)
        {
            return new ReplyAttachment(
                DiscussionMediaHelper.NormalizeOwnedUploadUrl(imageUrl!, userId, "images"),
                null,
                null,
                null,
                null,
                null);
        }

        if (hasVideo)
        {
            return new ReplyAttachment(
                null,
                DiscussionMediaHelper.NormalizeOwnedUploadUrl(videoUrl!, userId, "videos"),
                null,
                null,
                null,
                null);
        }

        if (hasLocation)
        {
            var lat = location!.Latitude;
            var lng = location.Longitude;
            if (lat is < -90 or > 90 || lng is < -180 or > 180)
                throw new ArgumentException("Coordonnées de lieu invalides.");

            var placeName = string.IsNullOrWhiteSpace(location.PlaceName)
                ? null
                : Truncate(location.PlaceName.Trim(), 200);
            var address = string.IsNullOrWhiteSpace(location.Address)
                ? null
                : Truncate(location.Address.Trim(), 300);

            return new ReplyAttachment(null, null, lat, lng, placeName, address);
        }

        return new ReplyAttachment(null, null, null, null, null, null);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    internal static DiscussionDto ToListDto(Discussion d, bool isLiked = false)
    {
        var images = DiscussionMediaHelper.ParseUrlList(d.ImageUrlsJson);
        var videos = DiscussionMediaHelper.ParseUrlList(d.VideoUrlsJson);
        return new DiscussionDto(
            d.Id,
            d.Title,
            d.Body,
            d.User.DisplayName,
            TimeHelpers.Initials(d.User.DisplayName),
            d.ReplyCount,
            TimeHelpers.FormatRelative(DiscussionSourceMapper.SortDate(d)),
            d.Category?.Slug,
            d.LikeCount,
            d.ViewCount,
            isLiked,
            d.IsExternal,
            DiscussionSourceMapper.ToSourceDto(d),
            d.Community is null ? null : CommunitySlugHelper.ToRouteSlug(d.Community.Slug),
            DiscussionMediaHelper.ToMediaDtos(images, videos));
    }

    private async Task<Guid?> ResolveCommunityIdAsync(string? communitySlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(communitySlug))
            return null;

        var slug = CommunitySlugHelper.Normalize(communitySlug);
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug, ct)
            ?? throw new ArgumentException($"Communauté k/{slug} introuvable.");
        return community.Id;
    }

    private async Task DeleteStoredMediaAsync(Discussion discussion, CancellationToken ct)
    {
        foreach (var url in DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson))
            await TryDeleteUploadAsync(url, ct);
        foreach (var url in DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson))
            await TryDeleteUploadAsync(url, ct);
    }

    private async Task TryDeleteUploadAsync(string url, CancellationToken ct)
    {
        try
        {
            if (url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                await storage.DeleteIfExistsAsync(url, ct);
        }
        catch (ArgumentException)
        {
            // Ignore invalid or external URLs.
        }
    }

    private async Task<Category> AssignTopicCategoryAsync(string text, CancellationToken ct)
    {
        var topicCategories = await db.Categories
            .AsNoTracking()
            .Where(c => c.IsDiscussionTopic)
            .ToListAsync(ct);

        var analysis = await openAi.AnalyzeDiscussionAsync(text, topicCategories, ct);
        return await AiDiscussionCategoryCatalog.GetOrCreateAsync(db, analysis.TopicSlug, cache, ct);
    }
}

public static class TimeHelpers
{
    public static string FormatRelative(DateTime createdAt)
    {
        var diff = DateTime.UtcNow - createdAt;
        if (diff.TotalMinutes < 60) return $"Il y a {Math.Max(1, (int)diff.TotalMinutes)} min";
        if (diff.TotalHours < 24) return $"Il y a {(int)diff.TotalHours} h";
        if (diff.TotalDays < 7) return diff.TotalDays < 2 ? "Hier" : $"Il y a {(int)diff.TotalDays} j";
        return createdAt.ToString("d MMM yyyy");
    }

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            : name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
    }
}
