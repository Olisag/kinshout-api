using System.Security.Cryptography;
using System.Text;
using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers;

namespace Kinshout.ExternalImporter.Import;

public static class AdvertMapper
{
    public static ImportExternalAdvertDto? ToImportDto(SourceFeedAdvert feed, ExternalProviderSettings provider, DateTime now)
    {
        var externalUrl = Clean(feed.ExternalUrl);
        var title = Clean(feed.Title);
        if (string.IsNullOrWhiteSpace(externalUrl) || string.IsNullOrWhiteSpace(title))
            return null;

        var externalId = Clean(feed.ExternalId) ?? StableId(externalUrl);
        var city = Clean(feed.Location?.City) ?? provider.DefaultCity;
        var commune = Clean(feed.Location?.Commune) ?? provider.DefaultCommune;
        var formattedLocation = Clean(feed.Location?.Formatted)
            ?? string.Join(", ", new[] { commune, city }.Where(x => !string.IsNullOrWhiteSpace(x)));

        return new ImportExternalAdvertDto(
            Source: new ImportExternalAdvertSourceDto(
                Provider: provider.Provider,
                ProviderName: provider.ProviderName,
                ExternalId: externalId,
                ExternalUrl: externalUrl,
                ImportedAt: now,
                LastSeenAt: now,
                FirstSeenAt: null),
            Category: Clean(feed.Category) ?? provider.DefaultCategory,
            Subcategory: Clean(feed.Subcategory) ?? provider.DefaultSubcategory,
            Title: title,
            Price: feed.Price is null
                ? null
                : new ImportExternalAdvertPriceDto(
                    feed.Price.Amount,
                    Clean(feed.Price.Currency),
                    Clean(feed.Price.Formatted),
                    Clean(feed.Price.Period),
                    feed.Price.Negotiable),
            Location: new ImportExternalAdvertLocationDto(
                city,
                commune,
                Clean(feed.Location?.Neighborhood),
                Clean(feed.Location?.Address),
                string.IsNullOrWhiteSpace(formattedLocation) ? city : formattedLocation),
            Details: feed.Details is null
                ? null
                : new ImportExternalAdvertDetailsDto(
                    feed.Details.Bedrooms,
                    feed.Details.Bathrooms,
                    feed.Details.Area,
                    feed.Details.Furnished,
                    feed.Details.Floor,
                    Clean(feed.Details.PropertyType),
                    Clean(feed.Details.Condition),
                    feed.Details.Parking,
                    feed.Details.PetFriendly,
                    feed.Details.YearBuilt),
            Description: Clean(feed.Description) ?? Clean(feed.Summary) ?? title,
            Images: feed.Images?.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct().Take(10).ToList(),
            Contact: feed.Contact is null
                ? null
                : new ImportExternalAdvertContactDto(
                    Clean(feed.Contact.SellerName),
                    Clean(feed.Contact.SellerProfileUrl),
                    Clean(feed.Contact.Phone),
                    Clean(feed.Contact.WhatsApp),
                    Clean(feed.Contact.PreferredContact),
                    feed.Contact.IsPubliclyListed),
            Status: Clean(feed.Status) ?? "active",
            PublishedAt: feed.PublishedAt,
            Modality: Clean(feed.Modality) ?? provider.DefaultModality,
            Ai: new ImportExternalAdvertAiDto(
                feed.Tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct().Take(12).ToList(),
                Clean(feed.Summary),
                feed.Intent?.Where(intent => !string.IsNullOrWhiteSpace(intent)).Select(intent => intent.Trim()).Distinct().ToList()),
            DuplicateGroupId: Clean(feed.DuplicateGroupId));
    }

    private static string StableId(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }
}
