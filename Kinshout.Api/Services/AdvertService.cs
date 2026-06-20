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
    Task<AdvertDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResultDto<AdvertDto>> ListAsync(
        Guid? categoryId = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        CancellationToken ct = default);
    Task<IReadOnlyList<AdvertDto>> ListMineAsync(Guid userId, CancellationToken ct = default);
}

public class AdvertService(
    KinshoutDbContext db,
    IOpenAiService openAi,
    IAdvertModerationService moderation,
    IWebHostEnvironment env) : IAdvertService
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

    public async Task<AdvertDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var updated = await db.Adverts
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.ViewCount, a => a.ViewCount + 1), ct);
        if (updated == 0)
            return null;

        var advert = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .FirstAsync(a => a.Id == id, ct);
        return ToDto(advert);
    }

    public async Task<PagedResultDto<AdvertDto>> ListAsync(
        Guid? categoryId = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);

        var query = db.Adverts.AsNoTracking().Include(a => a.Category).Include(a => a.User).Where(a => a.IsPublished);
        if (categoryId.HasValue)
            query = query.Where(a => a.CategoryId == categoryId.Value);

        var ordered = ListSortHelper.IsPopular(sort)
            ? query.OrderByDescending(a => a.ViewCount).ThenByDescending(a => a.CreatedAt)
            : query.OrderByDescending(a => a.CreatedAt);

        var total = await ordered.CountAsync(ct);
        var items = await ordered
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        return PagingHelper.Create(items.Select(ToDto).ToList(), normalizedPage, normalizedPageSize, total);
    }

    public async Task<IReadOnlyList<AdvertDto>> ListMineAsync(Guid userId, CancellationToken ct = default)
    {
        var items = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    internal static AdvertDto ToDto(Advert advert)
    {
        var tags = JsonSerializer.Deserialize<List<string>>(advert.TagsJson) ?? [];
        var imageUrls = JsonSerializer.Deserialize<List<string>>(advert.ImageUrlsJson) ?? [];
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
            advert.User?.WhatsAppNumber,
            tags,
            TimeHelpers.FormatRelative(advert.CreatedAt),
            advert.AiConfidence,
            advert.AiSummary
        );
    }

    private async Task ReModerateStoredImagesAsync(IReadOnlyList<string> imageUrls, CancellationToken ct)
    {
        foreach (var url in imageUrls)
        {
            var physicalPath = ResolveUploadPath(url);
            if (!File.Exists(physicalPath))
                throw new ArgumentException("Photo introuvable. Téléversez à nouveau vos images.");

            await using var stream = File.OpenRead(physicalPath);
            var contentType = GetContentTypeFromPath(physicalPath);
            await moderation.EnsureImageAllowedAsync(stream, contentType, ct);
        }
    }

    private string ResolveUploadPath(string url)
    {
        if (!url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("URL de photo invalide.");

        var relative = url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, relative));
        var uploadsRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads"));

        if (!fullPath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("URL de photo invalide.");

        return fullPath;
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

    private static string GetContentTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };

    private static AdvertIntent ParseIntent(string intent) =>
        intent.ToLowerInvariant() switch
        {
            "offre" => AdvertIntent.Offre,
            "discussion" => AdvertIntent.Discussion,
            _ => AdvertIntent.Demande,
        };
}
