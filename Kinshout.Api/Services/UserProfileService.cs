using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IUserProfileService
{
    Task<PublicUserProfileDto?> GetPublicProfileAsync(Guid userId, CancellationToken ct = default);
    Task<PagedResultDto<AdvertDto>> ListPublicAdvertsAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        Guid? viewerUserId = null,
        CancellationToken ct = default);
}

public class UserProfileService(KinshoutDbContext db, IUploadUrlResolver uploadUrls) : IUserProfileService
{
    public async Task<PublicUserProfileDto?> GetPublicProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsProfilePublic)
            .Select(u => new
            {
                User = u,
                PublishedCount = u.Adverts.Count(a => a.IsPublished),
            })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : ToPublicProfile(row.User, row.PublishedCount);
    }

    private PublicUserProfileDto ToPublicProfile(User user, int publishedAdvertCount) =>
        new(
            user.Id,
            user.DisplayName,
            uploadUrls.ToPublicUrl(user.AvatarUrl),
            $"Membre depuis {user.CreatedAt:MMM yyyy}",
            publishedAdvertCount);

    public async Task<PagedResultDto<AdvertDto>> ListPublicAdvertsAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        Guid? viewerUserId = null,
        CancellationToken ct = default)
    {
        var isPublic = await db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.IsProfilePublic, ct);
        if (!isPublic)
            throw new KeyNotFoundException("Profil introuvable.");

        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var query = db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.UserId == userId && a.IsPublished)
            .OrderByDescending(a => a.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var savedIds = await AdvertService.LoadSavedAdvertIdsAsync(
            db,
            viewerUserId,
            items.Select(a => a.Id),
            ct);

        return PagingHelper.Create(
            AdvertService.ToDtos(items, savedIds),
            normalizedPage,
            normalizedPageSize,
            total);
    }
}
