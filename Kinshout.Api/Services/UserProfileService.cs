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
        CancellationToken ct = default);
}

public class UserProfileService(KinshoutDbContext db) : IUserProfileService
{
    public async Task<PublicUserProfileDto?> GetPublicProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsProfilePublic)
            return null;

        var publishedCount = await db.Adverts
            .AsNoTracking()
            .CountAsync(a => a.UserId == userId && a.IsPublished, ct);

        return ToPublicProfile(user, publishedCount);
    }

    public async Task<PagedResultDto<AdvertDto>> ListPublicAdvertsAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
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

        return PagingHelper.Create(
            items.Select(AdvertService.ToDto).ToList(),
            normalizedPage,
            normalizedPageSize,
            total);
    }

    internal static PublicUserProfileDto ToPublicProfile(User user, int publishedAdvertCount) =>
        new(
            user.Id,
            user.DisplayName,
            user.AvatarUrl,
            $"Membre depuis {user.CreatedAt:MMM yyyy}",
            publishedAdvertCount);
}
