using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IExternalAdvertImportService
{
    Task<ImportExternalAdvertsResponseDto> ImportAsync(
        IReadOnlyList<ImportExternalAdvertDto> adverts,
        CancellationToken ct = default);

    Task<IReadOnlyList<ImportKnownAdvertKeyDto>> GetKnownAdvertKeysAsync(CancellationToken ct = default);
}

public class ExternalAdvertImportService(
    KinshoutDbContext db,
    IExternalAdvertImageMirrorService imageMirror,
    IExternalAdvertImportEnrichmentService enrichment,
    Microsoft.Extensions.Options.IOptions<Kinshout.Api.Configuration.ImportSettings> importOptions) : IExternalAdvertImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly HashSet<string> _blockedProviders = BuildBlockedProviders(importOptions.Value.BlockedAdvertProviders);

    private static HashSet<string> BuildBlockedProviders(IEnumerable<string>? providers) =>
        providers?
            .Select(p => AdvertSourceProvider.Normalize(p))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    public async Task<ImportExternalAdvertsResponseDto> ImportAsync(
        IReadOnlyList<ImportExternalAdvertDto> adverts,
        CancellationToken ct = default)
    {
        if (adverts.Count == 0)
            throw new ArgumentException("Aucune annonce à importer.");

        var importUser = await ImportSeed.EnsureImportUserAsync(db, ct);

        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var skipped = 0;

        foreach (var item in adverts)
        {
            try
            {
                var provider = AdvertSourceProvider.Normalize(item.Source.Provider);
                if (_blockedProviders.Contains(provider))
                {
                    skipped++;
                    continue;
                }

                var outcome = await UpsertAsync(item, importUser, ct);
                await db.SaveChangesAsync(ct);
                switch (outcome)
                {
                    case UpsertOutcome.Created: created++; break;
                    case UpsertOutcome.Updated: updated++; break;
                    case UpsertOutcome.Unchanged: unchanged++; break;
                    case UpsertOutcome.Skipped: skipped++; break;
                }
            }
            catch (Exception)
            {
                skipped++;
                db.ChangeTracker.Clear();
            }
        }

        return new ImportExternalAdvertsResponseDto(created, updated, unchanged, skipped);
    }

    public async Task<IReadOnlyList<ImportKnownAdvertKeyDto>> GetKnownAdvertKeysAsync(CancellationToken ct = default)
    {
        return await db.Adverts
            .AsNoTracking()
            .Where(a => a.SourceProvider != null
                && a.SourceExternalId != null
                && a.SourceProvider != AdvertSourceProvider.Kinshout)
            .Select(a => new ImportKnownAdvertKeyDto(a.SourceProvider!, a.SourceExternalId!))
            .Distinct()
            .ToListAsync(ct);
    }

    private enum UpsertOutcome { Created, Updated, Unchanged, Skipped }

    private async Task<UpsertOutcome> UpsertAsync(
        ImportExternalAdvertDto item,
        User importUser,
        CancellationToken ct)
    {
        var provider = AdvertSourceProvider.Normalize(item.Source.Provider);
        if (string.IsNullOrWhiteSpace(item.Source.ExternalId))
            throw new ArgumentException("externalId requis.");

        var existing = await db.Adverts
            .FirstOrDefaultAsync(
                a => a.SourceProvider == provider && a.SourceExternalId == item.Source.ExternalId.Trim(),
                ct);

        var isActive = !item.Status.Equals("inactive", StringComparison.OrdinalIgnoreCase)
            && !item.Status.Equals("removed", StringComparison.OrdinalIgnoreCase);

        if (!isActive)
        {
            if (existing is null)
                return UpsertOutcome.Skipped;

            var oldImages = DeserializeImages(existing.ImageUrlsJson);
            await imageMirror.DeleteMirroredAsync(oldImages, ct);
            db.Adverts.Remove(existing);
            return UpsertOutcome.Updated;
        }

        if (string.IsNullOrWhiteSpace(item.Source.ExternalUrl))
            throw new ArgumentException("externalUrl requis.");

        if (string.IsNullOrWhiteSpace(item.Title))
            throw new ArgumentException("title requis.");

        var sourceImages = item.Images?
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Take(10)
            .ToList() ?? [];
        if (sourceImages.Count == 0)
            return UpsertOutcome.Skipped;

        var now = DateTime.UtcNow;
        var importedAt = item.Source.ImportedAt ?? now;
        var lastSeenAt = item.Source.LastSeenAt ?? importedAt;
        var firstSeenAt = item.Source.FirstSeenAt ?? importedAt;

        var category = await AiCategoryCatalog.GetOrCreateAsync(
            db,
            item.Subcategory,
            item.Category,
            ct: ct);

        var mirroredImages = await imageMirror.MirrorAsync(
            sourceImages,
            provider,
            item.Source.ExternalId.Trim(),
            importUser.Id,
            ct);

        if (mirroredImages.Count == 0)
            return UpsertOutcome.Skipped;

        var enriched = await enrichment.EnrichAsync(item, category, ct);
        var mapped = MapFields(
            item,
            provider,
            category,
            importUser.Id,
            importedAt,
            lastSeenAt,
            firstSeenAt,
            mirroredImages,
            enriched);

        if (existing is null)
        {
            db.Adverts.Add(mapped);
            return UpsertOutcome.Created;
        }

        if (AdvertSourceMapper.IsSameContent(existing, mapped))
        {
            existing.SourceLastSeenAt = lastSeenAt;
            existing.UpdatedAt = now;
            return UpsertOutcome.Unchanged;
        }

        if (existing.ImageUrlsJson != mapped.ImageUrlsJson)
        {
            var oldImages = DeserializeImages(existing.ImageUrlsJson);
            await imageMirror.DeleteMirroredAsync(oldImages, ct);
        }

        ApplyUpdate(existing, mapped, lastSeenAt);
        return UpsertOutcome.Updated;
    }

    private static List<string> DeserializeImages(string? json) =>
        JsonSerializer.Deserialize<List<string>>(json ?? "[]", JsonOptions) ?? [];

    private static Advert MapFields(
        ImportExternalAdvertDto item,
        string provider,
        Category category,
        Guid userId,
        DateTime importedAt,
        DateTime lastSeenAt,
        DateTime firstSeenAt,
        IReadOnlyList<string> images,
        ImportEnrichmentResult enriched)
    {
        var publishedAt = item.PublishedAt ?? importedAt;
        var tags = item.Ai?.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() ?? [];
        var imageList = images.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Take(10).ToList();

        return new Advert
        {
            UserId = userId,
            CategoryId = category.Id,
            Title = Truncate(item.Title.Trim(), 200) ?? string.Empty,
            Description = Truncate(enriched.Description, 4000) ?? string.Empty,
            Price = Truncate(FormatPrice(item.Price), 64),
            Location = Truncate(FormatLocation(item.Location), 120),
            Intent = enriched.Intent,
            ImageUrlsJson = JsonSerializer.Serialize(imageList, JsonOptions),
            TagsJson = JsonSerializer.Serialize(tags, JsonOptions),
            AiConfidence = 1,
            AiSummary = enriched.Summary,
            IsPublished = true,
            SourceProvider = provider,
            SourceProviderName = Truncate(string.IsNullOrWhiteSpace(item.Source.ProviderName)
                ? AdvertSourceProvider.DisplayName(provider)
                : item.Source.ProviderName.Trim(), 120),
            SourceExternalId = Truncate(item.Source.ExternalId.Trim(), 128),
            SourceExternalUrl = Truncate(item.Source.ExternalUrl.Trim(), 2048),
            SourceImportedAt = importedAt,
            SourceLastSeenAt = lastSeenAt,
            SourceFirstSeenAt = firstSeenAt,
            SubcategorySlug = Truncate(item.Subcategory?.Trim().ToLowerInvariant(), 80),
            DetailsJson = item.Details is null
                ? "{}"
                : JsonSerializer.Serialize(item.Details, JsonOptions),
            ContactJson = item.Contact is null
                ? "{}"
                : JsonSerializer.Serialize(item.Contact, JsonOptions),
            DuplicateGroupId = item.DuplicateGroupId?.Trim(),
            ExternalPublishedAt = publishedAt,
            CreatedAt = publishedAt,
            UpdatedAt = importedAt,
        };
    }

    private static void ApplyUpdate(Advert existing, Advert mapped, DateTime lastSeenAt)
    {
        existing.CategoryId = mapped.CategoryId;
        existing.Title = mapped.Title;
        existing.Description = mapped.Description;
        existing.Price = mapped.Price;
        existing.Location = mapped.Location;
        existing.Intent = mapped.Intent;
        existing.ImageUrlsJson = mapped.ImageUrlsJson;
        existing.TagsJson = mapped.TagsJson;
        existing.AiSummary = mapped.AiSummary;
        existing.IsPublished = true;
        existing.SourceProviderName = mapped.SourceProviderName;
        existing.SourceExternalUrl = mapped.SourceExternalUrl;
        existing.SourceLastSeenAt = lastSeenAt;
        existing.SubcategorySlug = mapped.SubcategorySlug;
        existing.DetailsJson = mapped.DetailsJson;
        existing.ContactJson = mapped.ContactJson;
        existing.DuplicateGroupId = mapped.DuplicateGroupId;
        existing.ExternalPublishedAt = mapped.ExternalPublishedAt;
        existing.UpdatedAt = DateTime.UtcNow;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }

    private static string? FormatPrice(ImportExternalAdvertPriceDto? price)
    {
        if (price is null)
            return null;

        if (!string.IsNullOrWhiteSpace(price.Formatted))
            return price.Formatted.Trim();

        if (price.Amount is null)
            return null;

        var currency = string.IsNullOrWhiteSpace(price.Currency) ? "USD" : price.Currency.Trim().ToUpperInvariant();
        var formatted = currency switch
        {
            "USD" => $"$ {price.Amount:N0}",
            "CDF" => $"{price.Amount:N0} FC",
            _ => $"{price.Amount:N0} {currency}",
        };

        if (price.Period?.Equals("monthly", StringComparison.OrdinalIgnoreCase) == true)
            formatted += " / mois";

        if (price.Negotiable)
            formatted += " · Négociable";

        return formatted;
    }

    private static string? FormatLocation(ImportExternalAdvertLocationDto? location)
    {
        if (location is null)
            return "Kinshasa";

        if (!string.IsNullOrWhiteSpace(location.Formatted))
            return location.Formatted.Trim();

        var parts = new[] { location.Commune, location.City }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct()
            .ToList();

        return parts.Count > 0 ? string.Join(", ", parts) : "Kinshasa";
    }
}

internal static class AdvertSourceMapper
{
    public static bool IsSameContent(Advert existing, Advert mapped) =>
        existing.Title == mapped.Title
        && existing.Description == mapped.Description
        && existing.Price == mapped.Price
        && existing.Location == mapped.Location
        && existing.Intent == mapped.Intent
        && existing.ImageUrlsJson == mapped.ImageUrlsJson
        && existing.DetailsJson == mapped.DetailsJson
        && existing.ContactJson == mapped.ContactJson;

    public static AdvertSourceDto? ToSourceDto(Advert advert)
    {
        if (!advert.IsExternal || string.IsNullOrWhiteSpace(advert.SourceProvider))
            return null;

        return new AdvertSourceDto(
            advert.SourceProvider,
            advert.SourceProviderName ?? AdvertSourceProvider.DisplayName(advert.SourceProvider),
            advert.SourceExternalId ?? string.Empty,
            advert.SourceExternalUrl ?? string.Empty,
            advert.SourceImportedAt ?? advert.CreatedAt,
            advert.SourceLastSeenAt ?? advert.UpdatedAt,
            advert.SourceFirstSeenAt ?? advert.CreatedAt);
    }

    public static AdvertDetailsDto? ToDetailsDto(Advert advert)
    {
        if (string.IsNullOrWhiteSpace(advert.DetailsJson) || advert.DetailsJson == "{}")
            return null;

        return JsonSerializer.Deserialize<AdvertDetailsDto>(advert.DetailsJson);
    }

    public static AdvertContactDto? ToContactDto(Advert advert)
    {
        if (string.IsNullOrWhiteSpace(advert.ContactJson) || advert.ContactJson == "{}")
            return null;

        var contact = JsonSerializer.Deserialize<AdvertContactDto>(advert.ContactJson);
        if (contact is null)
            return null;

        if (advert.IsExternal && string.IsNullOrWhiteSpace(contact.WhatsApp) && !string.IsNullOrWhiteSpace(contact.Phone))
            return contact with { WhatsApp = contact.Phone };

        return contact;
    }

    public static string? NormalizeListFilter(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var normalized = source.Trim().ToLowerInvariant();
        return normalized switch
        {
            "all" or "toutes" => null,
            "kinshout" => AdvertSourceProvider.Kinshout,
            "external" or "sources_externes" or "externes" => "external",
            _ => AdvertSourceProvider.Normalize(normalized),
        };
    }

    public static IQueryable<Advert> ApplySourceFilter(IQueryable<Advert> query, string? sourceFilter)
    {
        if (string.IsNullOrWhiteSpace(sourceFilter))
            return query;

        if (sourceFilter == "external")
        {
            return query.Where(a => a.SourceProvider != null
                && a.SourceProvider != AdvertSourceProvider.Kinshout);
        }

        if (sourceFilter == AdvertSourceProvider.Kinshout)
        {
            return query.Where(a => a.SourceProvider == null
                || a.SourceProvider == AdvertSourceProvider.Kinshout);
        }

        return query.Where(a => a.SourceProvider == sourceFilter);
    }

    public static DateTime SortDate(Advert advert) =>
        advert.ExternalPublishedAt ?? advert.CreatedAt;
}
