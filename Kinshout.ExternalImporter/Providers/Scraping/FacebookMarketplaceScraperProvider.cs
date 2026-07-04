using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed class FacebookMarketplaceScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    public string Name => settings.Name;

    public async Task<IReadOnlyList<SourceFeedAdvert>> FetchAsync(CancellationToken ct)
    {
        var client = new SociaVaultClient(http, settings);
        var (lat, lng) = await client.ResolveLocationAsync(ct);
        var queries = CollectSearchQueries();
        var stubs = new Dictionary<string, SourceFeedAdvert>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            string? cursor = null;

            for (var page = 0; page < Math.Max(1, settings.MaxPages); page++)
            {
                ct.ThrowIfCancellationRequested();
                if (page > 0)
                    await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);

                using var doc = await client.SearchAsync(query, lat, lng, cursor, ct);
                SociaVaultClient.EnsureSuccess(doc);

                var data = doc.RootElement.TryGetProperty("data", out var dataNode) ? dataNode : doc.RootElement;
                var listings = SociaVaultClient.GetListingsContainer(data, "listings").ToList();
                if (listings.Count == 0)
                    break;

                foreach (var listing in listings)
                    AddListingStub(stubs, listing, settings);

                cursor = SociaVaultClient.ReadString(data, "cursor");
                var hasNext = data.TryGetProperty("has_next_page", out var hasNextNode)
                    && hasNextNode.ValueKind == JsonValueKind.True;

                if (string.IsNullOrWhiteSpace(cursor) || !hasNext)
                    break;
            }
        }

        if (stubs.Count == 0)
            Console.WriteLine("  SociaVault Facebook Marketplace: no listings found.");

        if (!settings.FetchDetails)
            return stubs.Values.ToList();

        var enriched = new List<SourceFeedAdvert>();
        foreach (var stub in stubs.Values)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);
                using var doc = await client.GetItemAsync(stub.ExternalId!, ct);
                SociaVaultClient.EnsureSuccess(doc);
                var item = doc.RootElement.TryGetProperty("data", out var dataNode) ? dataNode : doc.RootElement;
                enriched.Add(EnrichFromItem(stub, item, settings));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SociaVault item detail failed for {stub.ExternalId}: {ex.Message}");
                enriched.Add(stub);
            }
        }

        return enriched
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalUrl) && !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private List<string> CollectSearchQueries()
    {
        if (settings.SearchQueries.Count > 0)
            return settings.SearchQueries.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.RecentUrl) && !settings.RecentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            queries.Add(settings.RecentUrl);
        if (!string.IsNullOrWhiteSpace(settings.PopularUrl) && !settings.PopularUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            queries.Add(settings.PopularUrl);

        queries.AddRange(settings.ExtraListingUrls.Where(q => !string.IsNullOrWhiteSpace(q) && !q.StartsWith("http", StringComparison.OrdinalIgnoreCase)));

        if (queries.Count == 0)
        {
            queries.AddRange([
                "appartement",
                "maison",
                "immobilier",
                "parcelle",
            ]);
        }

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddListingStub(
        Dictionary<string, SourceFeedAdvert> stubs,
        JsonElement listing,
        ExternalProviderSettings settings)
    {
        if (listing.TryGetProperty("is_sold", out var soldNode) && soldNode.ValueKind == JsonValueKind.True)
            return;

        var id = SociaVaultClient.ReadString(listing, "id");
        var title = SociaVaultClient.ReadString(listing, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return;

        var externalUrl = SociaVaultClient.ReadString(listing, "url")
            ?? $"https://www.facebook.com/marketplace/item/{id}/";

        var price = listing.TryGetProperty("price", out var priceNode)
            ? new FeedPrice
            {
                Amount = SociaVaultClient.ReadDecimal(priceNode, "amount"),
                Currency = InferCurrency(priceNode),
                Formatted = SociaVaultClient.ReadString(priceNode, "formatted_amount")
                    ?? SociaVaultClient.ReadString(priceNode, "formatted_amount_zeros_stripped"),
            }
            : null;

        var locationText = listing.TryGetProperty("location", out var locationNode)
            ? SociaVaultClient.ReadString(locationNode, "display_name")
                ?? SociaVaultClient.ReadString(locationNode, "city")
            : null;

        var image = listing.TryGetProperty("primary_photo", out var photoNode)
            ? SociaVaultClient.ReadString(photoNode, "url")
            : null;

        stubs[id] = new SourceFeedAdvert
        {
            ExternalId = id,
            ExternalUrl = externalUrl,
            Title = title,
            Description = title,
            Summary = title,
            Category = "immobilier",
            Price = price,
            Location = new FeedLocation
            {
                City = settings.DefaultCity,
                Commune = ExtractCommune(locationText),
                Formatted = string.IsNullOrWhiteSpace(locationText) ? settings.DefaultCity : locationText,
            },
            Images = string.IsNullOrWhiteSpace(image) ? null : [image],
            Modality = InferModality(title),
            Tags = ["facebook", "marketplace"],
        };
    }

    private static SourceFeedAdvert EnrichFromItem(
        SourceFeedAdvert stub,
        JsonElement item,
        ExternalProviderSettings settings)
    {
        var title = SociaVaultClient.ReadString(item, "title") ?? stub.Title;
        var description = SociaVaultClient.ReadString(item, "description") ?? stub.Description;
        var locationText = SociaVaultClient.ReadString(item, "location_text")
            ?? stub.Location?.Formatted;

        FeedPrice? price = null;
        if (item.TryGetProperty("price", out var priceNode))
        {
            price = new FeedPrice
            {
                Amount = SociaVaultClient.ReadDecimal(priceNode, "amount"),
                Currency = SociaVaultClient.ReadString(priceNode, "currency") ?? InferCurrency(priceNode),
                Formatted = SociaVaultClient.ReadString(priceNode, "formatted_amount_zeros_stripped")
                    ?? SociaVaultClient.ReadString(priceNode, "formatted_amount"),
            };
        }

        var images = SociaVaultClient.GetListingsContainer(item, "photos")
            .Select(photo => SociaVaultClient.ReadString(photo, "url"))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (images.Count == 0 && stub.Images is { Count: > 0 })
            images = stub.Images.ToList();

        var propertyType = SociaVaultClient.GetListingsContainer(item, "attributes")
            .Select(attr => SociaVaultClient.ReadString(attr, "label"))
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label));

        var sellerName = item.TryGetProperty("seller", out var sellerNode) && sellerNode.ValueKind == JsonValueKind.Object
            ? SociaVaultClient.ReadString(sellerNode, "name")
            : null;

        stub.Title = title;
        stub.Description = description;
        stub.Summary = description;
        stub.PublishedAt = SociaVaultClient.ReadDate(item, "creation_time");
        stub.Price = price ?? stub.Price;
        stub.Images = images;
        stub.Location = new FeedLocation
        {
            City = settings.DefaultCity,
            Commune = ExtractCommune(locationText),
            Formatted = string.IsNullOrWhiteSpace(locationText) ? settings.DefaultCity : locationText,
        };
        stub.Details = new FeedDetails
        {
            PropertyType = propertyType,
            Condition = ReadCondition(item),
        };
        stub.Contact = sellerName is null
            ? stub.Contact
            : new FeedContact
            {
                SellerName = sellerName,
                IsPubliclyListed = false,
            };
        stub.Modality = InferModality(title, description);
        stub.Subcategory = MapSubcategory(title, stub.Modality);
        stub.Status = item.TryGetProperty("is_sold", out var soldNode) && soldNode.ValueKind == JsonValueKind.True
            ? "sold"
            : "active";

        return stub;
    }

    private static string InferCurrency(JsonElement priceNode) =>
        SociaVaultClient.ReadString(priceNode, "currency")
        ?? (SociaVaultClient.ReadString(priceNode, "formatted_amount")?.Contains('$') == true ? "USD" : null)
        ?? "USD";

    private static string InferModality(string? title, string? description = null)
    {
        var text = $"{title} {description}";
        if (text.Contains("louer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("location", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rent", StringComparison.OrdinalIgnoreCase))
            return "rent";

        if (text.Contains("vendre", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vente", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sale", StringComparison.OrdinalIgnoreCase))
            return "sale";

        return "rent";
    }

    private static string? ExtractCommune(string? locationText)
    {
        if (string.IsNullOrWhiteSpace(locationText))
            return null;

        var communes = new[]
        {
            "Gombe", "Ngaliema", "Limete", "Bandalungwa", "Kalamu", "Macampagne", "Masina", "Kinshasa",
        };

        return communes.FirstOrDefault(c => locationText.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MapSubcategory(string? title, string modality)
    {
        var text = title?.ToLowerInvariant() ?? "";
        if (text.Contains("appartement") || text.Contains("apartment"))
            return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
        if (text.Contains("parcelle") || text.Contains("terrain"))
            return "parcelle";
        if (text.Contains("maison") || text.Contains("villa"))
            return modality == "rent" ? "maison_a_louer" : "maison_a_vendre";
        return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
    }

    private static string? ReadCondition(JsonElement item) =>
        SociaVaultClient.GetListingsContainer(item, "attributes")
            .FirstOrDefault(attr => string.Equals(
                SociaVaultClient.ReadString(attr, "attribute_name"),
                "Condition",
                StringComparison.OrdinalIgnoreCase)) is { } condition
            ? SociaVaultClient.ReadString(condition, "label")
            : null;
}
