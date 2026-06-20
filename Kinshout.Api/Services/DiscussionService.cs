using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IDiscussionService
{
    Task<PagedResultDto<DiscussionDto>> ListAsync(
        string? query = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        CancellationToken ct = default);
    Task<DiscussionDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<DiscussionDto> CreateAsync(Guid userId, CreateDiscussionRequestDto request, CancellationToken ct = default);
    Task<DiscussionReplyDto> AddReplyAsync(Guid userId, Guid discussionId, CreateReplyRequestDto request, CancellationToken ct = default);
}

public class DiscussionService(
    KinshoutDbContext db,
    IOpenAiService openAi,
    IAdvertModerationService moderation) : IDiscussionService
{
    public async Task<PagedResultDto<DiscussionDto>> ListAsync(
        string? query = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var q = db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .Include(d => d.Replies)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var lower = query.ToLowerInvariant();
            q = q.Where(d => d.Title.ToLower().Contains(lower) || d.Body.ToLower().Contains(lower));
        }

        var ordered = ListSortHelper.IsPopular(sort)
            ? q.OrderByDescending(d => d.Replies.Count).ThenByDescending(d => d.CreatedAt)
            : q.OrderByDescending(d => d.CreatedAt);

        var total = await ordered.CountAsync(ct);
        var items = await ordered
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        return PagingHelper.Create(items.Select(ToListDto).ToList(), normalizedPage, normalizedPageSize, total);
    }

    public async Task<DiscussionDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var d = await db.Discussions
            .AsNoTracking()
            .Include(x => x.User)
            .Include(x => x.Replies).ThenInclude(r => r.User)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (d is null) return null;

        return new DiscussionDetailDto(
            d.Id,
            d.Title,
            d.Body,
            d.User.DisplayName,
            TimeHelpers.Initials(d.User.DisplayName),
            TimeHelpers.FormatRelative(d.CreatedAt),
            d.Replies.OrderBy(r => r.CreatedAt).Select(r => new DiscussionReplyDto(
                r.Id,
                r.User.DisplayName,
                TimeHelpers.Initials(r.User.DisplayName),
                TimeHelpers.FormatRelative(r.CreatedAt),
                r.Body
            )).ToList()
        );
    }

    public async Task<DiscussionDto> CreateAsync(Guid userId, CreateDiscussionRequestDto request, CancellationToken ct = default)
    {
        await moderation.EnsureTextAllowedAsync($"{request.Title}\n{request.Body}", ct);

        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var analysis = await openAi.AnalyzeAdvertAsync($"{request.Title}. {request.Body}", categories, ct);
        var category = await CategoryResolver.ResolveOrCreateCategoryAsync(db, analysis, ct);

        var discussion = new Discussion
        {
            UserId = userId,
            CategoryId = category.Id,
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
        };

        db.Discussions.Add(discussion);
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        discussion.User = user;
        discussion.Category = category;
        discussion.Replies = [];
        return ToListDto(discussion);
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

        var reply = new DiscussionReply
        {
            DiscussionId = discussionId,
            UserId = userId,
            Body = request.Body.Trim(),
        };

        db.DiscussionReplies.Add(reply);
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        return new DiscussionReplyDto(
            reply.Id,
            user.DisplayName,
            TimeHelpers.Initials(user.DisplayName),
            TimeHelpers.FormatRelative(reply.CreatedAt),
            reply.Body
        );
    }

    private static DiscussionDto ToListDto(Discussion d) =>
        new(
            d.Id,
            d.Title,
            d.Body,
            d.User.DisplayName,
            TimeHelpers.Initials(d.User.DisplayName),
            d.Replies.Count,
            TimeHelpers.FormatRelative(d.CreatedAt),
            d.Category?.Slug
        );
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
