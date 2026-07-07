using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed class ApifyFacebookMarketplaceScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    public string Name => settings.Name;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken ct)
    {
        var client = new ApifyClient(http, settings);
        var input = BuildInput();
        using var doc = await client.RunActorAndGetDatasetAsync(input, ct);

        var adverts = new List<SourceFeedAdvert>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  Apify Facebook Marketplace: unexpected dataset format.");
            return ProviderFetchResult.From(adverts, seenIds);
        }

        var rawCount = doc.RootElement.GetArrayLength();
        var filteredOut = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var rawId = ReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(rawId))
                seenIds.Add(rawId);

            try
            {
                var advert = MapListing(item);
                if (advert is not null)
                    adverts.Add(advert);
                else
                    filteredOut++;
            }
            catch (Exception ex)
            {
                filteredOut++;
                Console.WriteLine($"  Apify listing parse skipped: {ex.Message}");
            }
        }

        if (adverts.Count == 0 && rawCount > 0)
            Console.WriteLine($"  Apify Facebook Marketplace: {rawCount} raw listings, {filteredOut} filtered out (non-Kinshasa).");
        else if (rawCount > 0)
            Console.WriteLine($"  Apify Facebook Marketplace: kept {adverts.Count}/{rawCount} Kinshasa listings ({filteredOut} filtered out).");

        var deduped = adverts
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return ProviderFetchResult.From(deduped, seenIds);
    }

    private object BuildInput()
    {
        var startUrls = CollectStartUrls()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(url => new { url })
            .ToList();

        if (startUrls.Count == 0)
        {
            var location = ResolveMarketplaceLocation();
            startUrls =
            [
                new { url = $"https://www.facebook.com/marketplace/{location}/search/?query=appartement" },
            ];
        }

        var resultsLimit = settings.ResultsLimit > 0
            ? settings.ResultsLimit
            : Math.Max(10, settings.MaxPages * 25);

        return new
        {
            startUrls,
            resultsLimit,
            includeListingDetails = settings.FetchDetails,
        };
    }

    private IEnumerable<string> CollectStartUrls()
    {
        foreach (var url in settings.ExtraListingUrls.Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            yield return url;

        if (!string.IsNullOrWhiteSpace(settings.RecentUrl)
            && settings.RecentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            yield return settings.RecentUrl;
        }

        if (!string.IsNullOrWhiteSpace(settings.PopularUrl)
            && settings.PopularUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            yield return settings.PopularUrl;
        }

        var location = ResolveMarketplaceLocation();
        var lat = settings.Latitude;
        var lng = settings.Longitude;
        var useNumericMarketplace = IsNumericMarketplaceId(location);

        foreach (var query in CollectSearchQueries())
        {
            var localizedQuery = useNumericMarketplace || query.Contains("kinshasa", StringComparison.OrdinalIgnoreCase)
                ? query
                : $"{query} kinshasa";

            if (!useNumericMarketplace && lat is { } latitude && lng is { } longitude)
            {
                yield return
                    $"https://www.facebook.com/marketplace/search/?query={Uri.EscapeDataString(localizedQuery)}&latitude={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                continue;
            }

            yield return $"https://www.facebook.com/marketplace/{location}/search/?query={Uri.EscapeDataString(localizedQuery)}";
        }

        foreach (var categoryPath in CollectMarketplaceCategories())
            yield return $"https://www.facebook.com/marketplace/{location}/{categoryPath.Trim().Trim('/')}";
    }

    private IEnumerable<string> CollectMarketplaceCategories()
    {
        foreach (var path in settings.ExtraListingUrls.Where(u => !u.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            yield return path;
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

        if (queries.Count == 0)
            queries.AddRange([
                "appartement", "maison", "immobilier", "parcelle",
                "iphone", "samsung", "ordinateur", "voiture", "moto",
                "emploi", "meuble", "groupe électrogène",
            ]);

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string ResolveMarketplaceLocation()
    {
        if (!string.IsNullOrWhiteSpace(settings.MarketplaceLocation))
            return settings.MarketplaceLocation.Trim().Trim('/');

        return settings.DefaultCity.Equals("Kinshasa", StringComparison.OrdinalIgnoreCase)
            ? "113766458633901"
            : settings.DefaultCity.Trim().ToLowerInvariant().Replace(' ', '-');
    }

    private static bool IsNumericMarketplaceId(string location) =>
        location.Length >= 8 && location.All(char.IsDigit);

    private SourceFeedAdvert? MapListing(JsonElement item)
    {
        if (item.TryGetProperty("is_sold", out var sold) && sold.ValueKind == JsonValueKind.True)
            return null;

        if (item.TryGetProperty("error", out _) || item.TryGetProperty("errorInfo", out _))
            return null;

        var id = ReadString(item, "id");
        var title = ReadString(item, "marketplace_listing_title")
            ?? ReadString(item, "title")
            ?? ReadString(item, "custom_title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var externalUrl = ReadString(item, "listingUrl")
            ?? ReadString(item, "url")
            ?? $"https://www.facebook.com/marketplace/item/{id}/";

        var description = ReadString(item, "description")
            ?? ReadString(item, "marketplace_listing_description")
            ?? title;

        var locationText = ReadNestedString(item, "location", "reverse_geocode", "city_page", "display_name")
            ?? ReadNestedString(item, "location", "reverse_geocode", "city")
            ?? ReadString(item, "location_text");

        var image = ReadNestedString(item, "primary_listing_photo", "image", "uri")
            ?? ReadNestedString(item, "primary_listing_photo", "photo_image_url")
            ?? ReadNestedString(item, "primaryPhoto", "photoImageUrl")
            ?? ReadString(item, "image");

        if (!KinshasaListingFilter.IsKinshasa(item, title, description, locationText))
            return null;

        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(image))
            images.Add(image);

        if (item.TryGetProperty("photos", out var photos) && photos.ValueKind == JsonValueKind.Array)
        {
            foreach (var photo in photos.EnumerateArray())
            {
                var photoUrl = ReadString(photo, "url") ?? ReadNestedString(photo, "image", "uri");
                if (!string.IsNullOrWhiteSpace(photoUrl))
                    images.Add(photoUrl);
            }
        }

        FeedPrice? price = null;
        if (item.TryGetProperty("listing_price", out var priceNode) || item.TryGetProperty("price", out priceNode))
        {
            price = new FeedPrice
            {
                Amount = ReadDecimal(priceNode, "amount"),
                Currency = ReadString(priceNode, "currency") ?? InferCurrency(priceNode),
                Formatted = ReadString(priceNode, "formatted_amount")
                    ?? ReadString(priceNode, "formatted_amount_zeros_stripped"),
            };
        }

        var sellerName = ReadNestedString(item, "marketplace_listing_seller", "name")
            ?? ReadNestedString(item, "seller", "name");

        var category = ListingCategoryMapper.InferFromText(title, description, settings.DefaultCategory);

        return new SourceFeedAdvert
        {
            ExternalId = id,
            ExternalUrl = externalUrl,
            Title = title,
            Description = description,
            Summary = description,
            Category = category.Category,
            Subcategory = category.Subcategory ?? MapSubcategory(title, category.Modality),
            Status = item.TryGetProperty("is_sold", out var isSold) && isSold.ValueKind == JsonValueKind.True
                ? "sold"
                : "active",
            PublishedAt = ReadDate(item, "creation_time") ?? ReadDate(item, "listingTime"),
            Price = price,
            Location = new FeedLocation
            {
                City = settings.DefaultCity,
                Commune = KinshasaListingFilter.ExtractCommune(locationText ?? title),
                Formatted = string.IsNullOrWhiteSpace(locationText) ? settings.DefaultCity : locationText,
            },
            Images = images.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            Contact = string.IsNullOrWhiteSpace(sellerName)
                ? null
                : new FeedContact
                {
                    SellerName = sellerName,
                    IsPubliclyListed = false,
                },
            Modality = category.Modality,
            Tags = ["facebook", "marketplace", "apify"],
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTime? ReadDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return HtmlScrapeHelpers.ParseLooseDate(value.GetString());

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
            return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;

        return null;
    }

    private static string InferCurrency(JsonElement priceNode)
    {
        var formatted = ReadString(priceNode, "formatted_amount")
            ?? ReadString(priceNode, "formatted_amount_zeros_stripped")
            ?? "";

        if (formatted.Contains('$') || formatted.Contains("USD", StringComparison.OrdinalIgnoreCase))
            return "USD";
        if (formatted.Contains("FC", StringComparison.OrdinalIgnoreCase) || formatted.Contains("CDF", StringComparison.OrdinalIgnoreCase))
            return "CDF";

        return ReadString(priceNode, "currency") ?? "USD";
    }

    private static string InferModality(string? title, string? description)
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
}
