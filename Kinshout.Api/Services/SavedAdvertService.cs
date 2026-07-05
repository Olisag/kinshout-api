using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ISavedAdvertService
{
    Task<AdvertDto> SaveAsync(Guid userId, Guid advertId, CancellationToken ct = default);
    Task<AdvertDto> UnsaveAsync(Guid userId, Guid advertId, CancellationToken ct = default);
    Task<PagedResultDto<AdvertDto>> ListSavedAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListSavedIdsAsync(Guid userId, CancellationToken ct = default);
}

public class SavedAdvertService(KinshoutDbContext db, IAdvertDtoMapper advertDtos) : ISavedAdvertService
{
    public async Task<AdvertDto> SaveAsync(Guid userId, Guid advertId, CancellationToken ct = default)
    {
        var advert = await db.Adverts.FirstOrDefaultAsync(a => a.Id == advertId && a.IsPublished, ct);
        if (advert is null)
            throw new KeyNotFoundException("Annonce introuvable.");

        var existing = await db.SavedAdverts
            .FirstOrDefaultAsync(s => s.UserId == userId && s.AdvertId == advertId, ct);
        if (existing is null)
        {
            db.SavedAdverts.Add(new SavedAdvert
            {
                UserId = userId,
                AdvertId = advertId,
                SavedAt = DateTime.UtcNow,
            });
            advert.LikeCount++;
            await db.SaveChangesAsync(ct);
        }

        return await LoadAdvertDtoAsync(advertId, isSaved: true, ct);
    }

    public async Task<AdvertDto> UnsaveAsync(Guid userId, Guid advertId, CancellationToken ct = default)
    {
        var saved = await db.SavedAdverts
            .FirstOrDefaultAsync(s => s.UserId == userId && s.AdvertId == advertId, ct);
        if (saved is not null)
        {
            var advert = await db.Adverts.FirstOrDefaultAsync(a => a.Id == advertId, ct);
            db.SavedAdverts.Remove(saved);
            if (advert is not null && advert.LikeCount > 0)
                advert.LikeCount--;
            await db.SaveChangesAsync(ct);
        }

        return await LoadAdvertDtoAsync(advertId, isSaved: false, ct);
    }

    public async Task<PagedResultDto<AdvertDto>> ListSavedAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var query = db.SavedAdverts
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Advert.IsPublished)
            .Include(s => s.Advert).ThenInclude(a => a.Category)
            .Include(s => s.Advert).ThenInclude(a => a.User)
            .OrderByDescending(s => s.SavedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(s => s.Advert)
            .ToListAsync(ct);

        return PagingHelper.Create(
            advertDtos.ToDtos(items, items.Select(a => a.Id).ToHashSet()),
            normalizedPage,
            normalizedPageSize,
            total);
    }

    public async Task<IReadOnlyList<Guid>> ListSavedIdsAsync(Guid userId, CancellationToken ct = default) =>
        await db.SavedAdverts
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Advert.IsPublished)
            .Select(s => s.AdvertId)
            .ToListAsync(ct);

    private async Task<AdvertDto> LoadAdvertDtoAsync(Guid advertId, bool isSaved, CancellationToken ct)
    {
        var advert = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == advertId && a.IsPublished, ct)
            ?? throw new KeyNotFoundException("Annonce introuvable.");

        return advertDtos.ToDto(advert, isSaved, includeDisplayUrls: true);
    }
}
