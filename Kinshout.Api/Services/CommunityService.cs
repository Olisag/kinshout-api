using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ICommunityService
{
    Task<PagedResultDto<CommunityDto>> ListAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task<CommunityDto?> GetBySlugAsync(string slugOrRoute, CancellationToken ct = default);
    Task<CommunityDto> CreateAsync(Guid userId, CreateCommunityRequestDto request, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, string slugOrRoute, CancellationToken ct = default);
}

public class CommunityService(KinshoutDbContext db) : ICommunityService
{
    public async Task<PagedResultDto<CommunityDto>> ListAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var query = db.Communities
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(c => new
            {
                Community = c,
                DiscussionCount = c.Discussions.Count,
            })
            .ToListAsync(ct);

        return PagingHelper.Create(
            items.Select(x => ToDto(x.Community, x.DiscussionCount)).ToList(),
            normalizedPage,
            normalizedPageSize,
            total);
    }

    public async Task<CommunityDto?> GetBySlugAsync(string slugOrRoute, CancellationToken ct = default)
    {
        var slug = CommunitySlugHelper.Normalize(slugOrRoute);
        var community = await db.Communities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (community is null)
            return null;

        var count = await db.Discussions.CountAsync(d => d.CommunityId == community.Id, ct);
        return ToDto(community, count);
    }

    public async Task<CommunityDto> CreateAsync(
        Guid userId,
        CreateCommunityRequestDto request,
        CancellationToken ct = default)
    {
        var slug = CommunitySlugHelper.Normalize(request.Slug);
        var name = string.IsNullOrWhiteSpace(request.Name) ? slug : request.Name.Trim();
        if (name.Length > 120)
            throw new ArgumentException("Le nom est trop long (max 120 caractères).");

        var description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();

        if (await db.Communities.AnyAsync(c => c.Slug == slug, ct))
            throw new ArgumentException($"La communauté k/{slug} existe déjà.");

        var community = new Community
        {
            Slug = slug,
            Name = name,
            Description = description,
            CreatedByUserId = userId,
        };

        db.Communities.Add(community);
        await db.SaveChangesAsync(ct);
        return ToDto(community, discussionCount: 0);
    }

    public async Task DeleteAsync(Guid userId, string slugOrRoute, CancellationToken ct = default)
    {
        var slug = CommunitySlugHelper.Normalize(slugOrRoute);
        var community = await db.Communities
            .FirstOrDefaultAsync(c => c.Slug == slug, ct)
            ?? throw new KeyNotFoundException("Communauté introuvable.");

        if (community.CreatedByUserId != userId)
            throw new UnauthorizedAccessException("Seul le créateur peut supprimer cette communauté.");

        var hasDiscussions = await db.Discussions.AnyAsync(d => d.CommunityId == community.Id, ct);
        if (hasDiscussions)
            throw new InvalidOperationException(
                "Impossible de supprimer une communauté qui contient encore des discussions.");

        db.Communities.Remove(community);
        await db.SaveChangesAsync(ct);
    }

    public static CommunityDto ToDto(Community community, int discussionCount) =>
        new(
            community.Id,
            CommunitySlugHelper.ToRouteSlug(community.Slug),
            community.Slug,
            community.Name,
            community.Description,
            discussionCount,
            community.CreatedByUserId,
            community.CreatedAt);
}
