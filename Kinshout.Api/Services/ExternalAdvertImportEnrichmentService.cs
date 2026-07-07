using Kinshout.Api.Configuration;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record ImportEnrichmentResult(string Description, AdvertIntent Intent, string? Summary);

public interface IExternalAdvertImportEnrichmentService
{
    Task<ImportEnrichmentResult> EnrichAsync(
        ImportExternalAdvertDto item,
        Category category,
        CancellationToken ct = default);
}

public sealed class ExternalAdvertImportEnrichmentService(
    IOpenAiService openAi,
    IOptions<ImportSettings> importOptions,
    ILogger<ExternalAdvertImportEnrichmentService> logger) : IExternalAdvertImportEnrichmentService
{
    public async Task<ImportEnrichmentResult> EnrichAsync(
        ImportExternalAdvertDto item,
        Category category,
        CancellationToken ct = default)
    {
        var heuristicIntent = ImportAdvertIntentResolver.Resolve(item);
        var description = item.Description?.Trim() ?? string.Empty;
        var summary = item.Ai?.Summary?.Trim();

        if (ImportDescriptionQuality.IsMeaningful(description, item.Title, summary))
            return new ImportEnrichmentResult(description, heuristicIntent, summary);

        if (importOptions.Value.EnrichDescriptionsWithAi)
        {
            try
            {
                var ai = await openAi.EnrichImportedAdvertAsync(item, category, ct);
                if (ai is not null && !string.IsNullOrWhiteSpace(ai.Description))
                {
                    return new ImportEnrichmentResult(
                        ai.Description.Trim(),
                        ParseIntent(ai.Intent) ?? heuristicIntent,
                        string.IsNullOrWhiteSpace(ai.Summary) ? summary : ai.Summary.Trim());
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI import enrichment failed for {ExternalId}", item.Source.ExternalId);
            }
        }

        var fallback = BuildStructuredDescription(item, category);
        return new ImportEnrichmentResult(fallback, heuristicIntent, summary);
    }

    internal static string BuildStructuredDescription(ImportExternalAdvertDto item, Category category)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Title))
            parts.Add(item.Title.Trim());

        var location = FormatLocation(item.Location);
        if (!string.IsNullOrWhiteSpace(location))
            parts.Add($"Situé à {location}.");

        var price = FormatPrice(item.Price);
        if (!string.IsNullOrWhiteSpace(price))
            parts.Add($"Prix: {price}.");

        var details = FormatDetails(item.Details);
        if (!string.IsNullOrWhiteSpace(details))
            parts.Add(details);

        if (!string.IsNullOrWhiteSpace(category.Label))
            parts.Add($"Catégorie: {category.Label}.");

        if (parts.Count == 0)
            return item.Title.Trim();

        return string.Join(' ', parts);
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
        return $"{price.Amount:N0} {currency}";
    }

    private static string? FormatLocation(ImportExternalAdvertLocationDto? location)
    {
        if (location is null)
            return null;

        if (!string.IsNullOrWhiteSpace(location.Formatted))
            return location.Formatted.Trim();

        var parts = new[] { location.Commune, location.City }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct();
        return string.Join(", ", parts);
    }

    private static string? FormatDetails(ImportExternalAdvertDetailsDto? details)
    {
        if (details is null)
            return null;

        var parts = new List<string>();
        if (details.Bedrooms is > 0)
            parts.Add($"{details.Bedrooms} chambre{(details.Bedrooms > 1 ? "s" : "")}");
        if (details.Bathrooms is > 0)
            parts.Add($"{details.Bathrooms} salle{(details.Bathrooms > 1 ? "s" : "")} de bain");
        if (details.Area is > 0)
            parts.Add($"{details.Area} m²");
        if (details.Furnished == true)
            parts.Add("meublé");
        if (!string.IsNullOrWhiteSpace(details.PropertyType))
            parts.Add(details.PropertyType.Trim());

        return parts.Count == 0 ? null : string.Join(", ", parts) + ".";
    }

    private static AdvertIntent? ParseIntent(string? intent) =>
        intent?.Trim().ToLowerInvariant() switch
        {
            "offre" => AdvertIntent.Offre,
            "demande" => AdvertIntent.Demande,
            _ => null,
        };
}
