using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ISavedAdvertService
{
    Task SaveAsync(Guid userId, Guid advertId, CancellationToken ct = default);
    Task UnsaveAsync(Guid userId, Guid advertId, CancellationToken ct = default);
    Task<PagedResultDto<AdvertDto>> ListSavedAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListSavedIdsAsync(Guid userId, CancellationToken ct = default);
}

public class SavedAdvertService(KinshoutDbContext db) : ISavedAdvertService
{
    public async Task SaveAsync(Guid userId, Guid advertId, CancellationToken ct = default)
    {
        var advert = await db.Adverts.FirstOrDefaultAsync(a => a.Id == advertId && a.IsPublished, ct);
        if (advert is null)
            throw new KeyNotFoundException("Annonce introuvable.");

        var existing = await db.SavedAdverts
            .FirstOrDefaultAsync(s => s.UserId == userId && s.AdvertId == advertId, ct);
        if (existing is not null)
            return;

        db.SavedAdverts.Add(new SavedAdvert
        {
            UserId = userId,
            AdvertId = advertId,
            SavedAt = DateTime.UtcNow,
        });
        advert.LikeCount++;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsaveAsync(Guid userId, Guid advertId, CancellationToken ct = default)
    {
        var saved = await db.SavedAdverts
            .FirstOrDefaultAsync(s => s.UserId == userId && s.AdvertId == advertId, ct);
        if (saved is null)
            return;

        var advert = await db.Adverts.FirstOrDefaultAsync(a => a.Id == advertId, ct);
        db.SavedAdverts.Remove(saved);
        if (advert is not null && advert.LikeCount > 0)
            advert.LikeCount--;
        await db.SaveChangesAsync(ct);
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
            items.Select(AdvertService.ToDto).ToList(),
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
}
