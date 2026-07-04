using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IAdvertService
{
    Task<AdvertDto> CreateAsync(Guid userId, CreateAdvertRequestDto request, CancellationToken ct = default);
    Task<AdvertDto> UpdateAsync(Guid userId, Guid advertId, UpdateAdvertRequestDto request, CancellationToken ct = default);
    Task<AdvertDto?> GetByIdAsync(Guid id, Guid? viewerUserId = null, CancellationToken ct = default);
    Task<PagedResultDto<AdvertDto>> ListAsync(
        Guid? categoryId = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        string? intent = null,
        string? source = null,
        Guid? viewerUserId = null,
        CancellationToken ct = default);
    Task<PagedResultDto<AdvertDto>> ListMineAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid advertId, CancellationToken ct = default);
}

public class AdvertService(
    KinshoutDbContext db,
    IOpenAiService openAi,
    IAdvertModerationService moderation,
    IUploadStorage storage) : IAdvertService
{
    private const int MaxImages = 10;

    public async Task<AdvertDto> CreateAsync(Guid userId, CreateAdvertRequestDto request, CancellationToken ct = default)
    {
        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Le texte de l'annonce est requis.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("Utilisateur introuvable.");

        if (string.IsNullOrWhiteSpace(user.WhatsAppNumber))
            throw new ArgumentException("Ajoutez votre numéro WhatsApp dans votre profil avant de publier.");

        await moderation.EnsureTextAllowedAsync(text, ct);

        var imageUrls = NormalizeImageUrls(request.ImageUrls);
        ValidateOwnedImageUrls(userId, imageUrls);
        await ReModerateStoredImagesAsync(imageUrls, ct);

        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var analysis = await openAi.AnalyzeAdvertAsync(text, categories, ct);
        var category = await CategoryResolver.ResolveOrCreateCategoryAsync(db, analysis, ct);

        var intent = ParseIntent(request.Intent ?? analysis.Intent);

        var advert = new Advert
        {
            UserId = userId,
            CategoryId = category.Id,
            Title = analysis.Title,
            Description = analysis.Description,
            Price = request.Price ?? analysis.Price,
            Location = request.Location ?? analysis.Location,
            Intent = intent,
            ImageUrlsJson = JsonSerializer.Serialize(imageUrls),
            ResumeUrl = string.IsNullOrWhiteSpace(request.ResumeUrl) ? null : request.ResumeUrl.Trim(),
            TagsJson = JsonSerializer.Serialize(analysis.Tags),
            AiConfidence = analysis.Confidence,
            AiSummary = analysis.Summary,
        };

        db.Adverts.Add(advert);
        await db.SaveChangesAsync(ct);

        advert.Category = category;
        advert.User = user;
        return ToDto(advert);
    }

    public async Task<AdvertDto> UpdateAsync(
        Guid userId,
        Guid advertId,
        UpdateAdvertRequestDto request,
        CancellationToken ct = default)
    {
        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Le texte de l'annonce est requis.");

        var advert = await db.Adverts
            .Include(a => a.Category)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == advertId && a.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Annonce introuvable.");

        var user = advert.User
            ?? await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("Utilisateur introuvable.");

        if (string.IsNullOrWhiteSpace(user.WhatsAppNumber))
            throw new ArgumentException("Ajoutez votre numéro WhatsApp dans votre profil avant de publier.");

        await moderation.EnsureTextAllowedAsync(text, ct);

        var imageUrls = NormalizeImageUrls(request.ImageUrls);
        ValidateOwnedImageUrls(userId, imageUrls);
        await ReModerateStoredImagesAsync(imageUrls, ct);

        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var analysis = await openAi.AnalyzeAdvertAsync(text, categories, ct);
        var category = await CategoryResolver.ResolveOrCreateCategoryAsync(db, analysis, ct);
        var intent = ParseIntent(request.Intent ?? analysis.Intent);

        advert.CategoryId = category.Id;
        advert.Title = analysis.Title;
        advert.Description = analysis.Description;
        advert.Price = request.Price ?? analysis.Price;
        advert.Location = request.Location ?? analysis.Location;
        advert.Intent = intent;
        advert.ImageUrlsJson = JsonSerializer.Serialize(imageUrls);
        advert.ResumeUrl = string.IsNullOrWhiteSpace(request.ResumeUrl) ? null : request.ResumeUrl.Trim();
        advert.TagsJson = JsonSerializer.Serialize(analysis.Tags);
        advert.AiConfidence = analysis.Confidence;
        advert.AiSummary = analysis.Summary;
        advert.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        advert.Category = category;
        advert.User = user;
        return ToDto(advert);
    }

    public async Task<AdvertDto?> GetByIdAsync(Guid id, Guid? viewerUserId = null, CancellationToken ct = default)
    {
        var advert = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id && a.IsPublished, ct);
        if (advert is null)
            return null;

        advert.ViewCount = await IncrementViewCountAsync(id, advert.ViewCount, ct);
        var isSaved = await IsSavedByUserAsync(db, viewerUserId, id, ct);
        return ToDto(advert, isSaved);
    }

    private async Task<int> IncrementViewCountAsync(Guid id, int currentCount, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.Adverts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (tracked is null)
                return currentCount;

            tracked.ViewCount++;
            await db.SaveChangesAsync(ct);
            return tracked.ViewCount;
        }

        await db.Adverts
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ViewCount, a => a.ViewCount + 1), ct);

        return currentCount + 1;
    }

    public async Task<PagedResultDto<AdvertDto>> ListAsync(
        Guid? categoryId = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        string? intent = null,
        string? source = null,
        Guid? viewerUserId = null,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var sourceFilter = AdvertSourceMapper.NormalizeListFilter(source);

        var query = db.Adverts.AsNoTracking().Include(a => a.Category).Include(a => a.User).Where(a => a.IsPublished);
        if (categoryId.HasValue)
            query = query.Where(a => a.CategoryId == categoryId.Value);

        if (!string.IsNullOrWhiteSpace(intent))
            query = query.Where(a => a.Intent == ParseListIntent(intent));

        query = AdvertSourceMapper.ApplySourceFilter(query, sourceFilter);

        var ordered = ListSortHelper.IsPopular(sort)
            ? query.OrderByDescending(a => a.ViewCount).ThenByDescending(a => a.ExternalPublishedAt ?? a.CreatedAt)
            : query.OrderByDescending(a => a.ExternalPublishedAt ?? a.CreatedAt);

        var total = await ordered.CountAsync(ct);
        var items = await ordered
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var savedIds = await LoadSavedAdvertIdsAsync(db, viewerUserId, items.Select(a => a.Id), ct);
        return PagingHelper.Create(ToDtos(items, savedIds), normalizedPage, normalizedPageSize, total);
    }

    public async Task<PagedResultDto<AdvertDto>> ListMineAsync(
        Guid userId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var query = db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.UpdatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        return PagingHelper.Create(items.Select(a => ToDto(a)).ToList(), normalizedPage, normalizedPageSize, total);
    }

    public async Task DeleteAsync(Guid userId, Guid advertId, CancellationToken ct = default)
    {
        var advert = await db.Adverts
            .FirstOrDefaultAsync(a => a.Id == advertId && a.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Annonce introuvable.");

        await DeleteUploadFiles(advert, ct);

        db.Adverts.Remove(advert);
        await db.SaveChangesAsync(ct);
    }

    internal static AdvertDto ToDto(Advert advert, bool isSaved = false)
    {
        var tags = JsonSerializer.Deserialize<List<string>>(advert.TagsJson) ?? [];
        var imageUrls = JsonSerializer.Deserialize<List<string>>(advert.ImageUrlsJson) ?? [];
        var contact = AdvertSourceMapper.ToContactDto(advert);
        var sortDate = AdvertSourceMapper.SortDate(advert);
        return new AdvertDto(
            advert.Id,
            advert.Title,
            advert.Description,
            advert.Price,
            advert.Location,
            advert.Intent.ToString().ToLowerInvariant(),
            advert.Category.Slug,
            advert.Category.Label,
            advert.Category.Icon,
            imageUrls,
            advert.ResumeUrl,
            contact?.WhatsApp ?? advert.User?.WhatsAppNumber,
            tags,
            TimeHelpers.FormatRelative(sortDate),
            advert.AiConfidence,
            advert.AiSummary,
            advert.ViewCount,
            advert.LikeCount,
            isSaved,
            advert.IsExternal,
            AdvertSourceMapper.ToSourceDto(advert),
            AdvertSourceMapper.ToDetailsDto(advert),
            contact);
    }

    internal static List<AdvertDto> ToDtos(IReadOnlyList<Advert> adverts, IReadOnlySet<Guid> savedIds) =>
        adverts.Select(a => ToDto(a, savedIds.Contains(a.Id))).ToList();

    internal static async Task<HashSet<Guid>> LoadSavedAdvertIdsAsync(
        KinshoutDbContext db,
        Guid? viewerUserId,
        IEnumerable<Guid> advertIds,
        CancellationToken ct)
    {
        if (viewerUserId is null)
            return [];

        var ids = advertIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var saved = await db.SavedAdverts
            .AsNoTracking()
            .Where(s => s.UserId == viewerUserId.Value && ids.Contains(s.AdvertId))
            .Select(s => s.AdvertId)
            .ToListAsync(ct);

        return saved.ToHashSet();
    }

    private static async Task<bool> IsSavedByUserAsync(
        KinshoutDbContext db,
        Guid? viewerUserId,
        Guid advertId,
        CancellationToken ct) =>
        viewerUserId is not null
        && await db.SavedAdverts
            .AsNoTracking()
            .AnyAsync(s => s.UserId == viewerUserId.Value && s.AdvertId == advertId, ct);

    private async Task ReModerateStoredImagesAsync(IReadOnlyList<string> imageUrls, CancellationToken ct)
    {
        foreach (var url in imageUrls)
        {
            if (!await storage.ExistsAsync(url, ct))
                throw new ArgumentException("Photo introuvable. Téléversez à nouveau vos images.");

            var file = await storage.OpenReadAsync(url, ct)
                ?? throw new ArgumentException("Photo introuvable. Téléversez à nouveau vos images.");

            await using (file.Stream)
                await moderation.EnsureImageAllowedAsync(file.Stream, file.ContentType, ct);
        }
    }

    private async Task DeleteUploadFiles(Advert advert, CancellationToken ct)
    {
        var imageUrls = JsonSerializer.Deserialize<List<string>>(advert.ImageUrlsJson) ?? [];
        foreach (var url in imageUrls)
            await TryDeleteUploadAsync(url, ct);

        if (!string.IsNullOrWhiteSpace(advert.ResumeUrl))
            await TryDeleteUploadAsync(advert.ResumeUrl, ct);
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

    private static void ValidateOwnedImageUrls(Guid userId, IReadOnlyList<string> imageUrls)
    {
        var prefix = $"/uploads/images/{userId:N}/";
        foreach (var url in imageUrls)
        {
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Seules vos photos téléversées sur Kinshout sont autorisées. Les liens Internet sont interdits.");
        }
    }

    private static List<string> NormalizeImageUrls(IReadOnlyList<string>? imageUrls)
    {
        if (imageUrls is null || imageUrls.Count == 0)
            return [];

        var normalized = imageUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > MaxImages)
            throw new ArgumentException($"Maximum {MaxImages} photos par annonce.");

        return normalized;
    }

    private static AdvertIntent ParseListIntent(string intent) =>
        intent.ToLowerInvariant() switch
        {
            "offre" => AdvertIntent.Offre,
            "demande" => AdvertIntent.Demande,
            _ => throw new ArgumentException("Le paramètre intent doit être offre ou demande."),
        };

    private static AdvertIntent ParseIntent(string intent) =>
        intent.ToLowerInvariant() switch
        {
            "offre" => AdvertIntent.Offre,
            "discussion" => AdvertIntent.Discussion,
            _ => AdvertIntent.Demande,
        };
}
