using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Import;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed partial class ZwandakoScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    private const string BaseUrl = "https://zwandako.com/";

    public string Name => settings.Name;

    public async Task<IReadOnlyList<SourceFeedAdvert>> FetchAsync(CancellationToken ct)
    {
        var stubs = new Dictionary<string, SourceFeedAdvert>(StringComparer.OrdinalIgnoreCase);

        await CollectFromFeedAsync(stubs, ct);
        await CollectFromSearchPagesAsync(stubs, ct);

        if (!settings.FetchDetails)
            return stubs.Values.ToList();

        var enriched = new List<SourceFeedAdvert>();
        foreach (var stub in stubs.Values)
        {
            ct.ThrowIfCancellationRequested();

            if (ImportAdvertKeys.IsKnown(settings.KnownAdvertKeys, settings.Provider, stub.ExternalId))
                continue;

            try
            {
                await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);
                var html = await ScraperHttp.FetchHtmlAsync(http, settings, stub.ExternalUrl!, BaseUrl, ct);
                enriched.Add(EnrichFromDetailPage(stub, html));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Zwandako detail failed for {stub.ExternalUrl}: {ex.Message}");
                enriched.Add(stub);
            }
        }

        return enriched
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalUrl) && !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private async Task CollectFromFeedAsync(Dictionary<string, SourceFeedAdvert> stubs, CancellationToken ct)
    {
        var feedUrl = string.IsNullOrWhiteSpace(settings.RecentUrl)
            ? $"{BaseUrl}immobilier/feed/"
            : settings.RecentUrl;

        try
        {
            var xml = await ScraperHttp.FetchHtmlAsync(http, settings, feedUrl, BaseUrl, ct);
            var document = XDocument.Parse(xml);
            XNamespace content = "http://purl.org/rss/1.0/modules/content/";

            foreach (var item in document.Descendants("item"))
            {
                var link = item.Element("link")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(link))
                    continue;

                var title = item.Element("title")?.Value.Trim();
                var descriptionHtml = item.Element("description")?.Value ?? item.Element(content + "encoded")?.Value;
                var description = StripHtml(descriptionHtml);
                var image = ExtractFirstImage(descriptionHtml);
                var publishedAt = HtmlScrapeHelpers.ParseLooseDate(item.Element("pubDate")?.Value);
                var externalId = PostIdRegex().Match(item.Element("guid")?.Value ?? link).Groups[1].Value;
                if (string.IsNullOrWhiteSpace(externalId))
                    externalId = SlugRegex().Match(link).Groups[1].Value;

                stubs[link] = new SourceFeedAdvert
                {
                    ExternalId = externalId,
                    ExternalUrl = link,
                    Title = title,
                    Description = description ?? title,
                    Summary = description,
                    Category = "immobilier",
                    PublishedAt = publishedAt,
                    Images = string.IsNullOrWhiteSpace(image) ? null : [image],
                    Location = new FeedLocation { City = "Kinshasa", Formatted = ExtractLocation(title) ?? "Kinshasa" },
                    Modality = InferModality(title, description),
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Zwandako feed failed ({feedUrl}): {ex.Message}");
        }
    }

    private async Task CollectFromSearchPagesAsync(Dictionary<string, SourceFeedAdvert> stubs, CancellationToken ct)
    {
        var searchUrl = settings.PopularUrl;
        if (string.IsNullOrWhiteSpace(searchUrl))
            searchUrl = $"{BaseUrl}resultats-de-recherche/?status%5B%5D=a-louer&location%5B%5D=kinshasa";

        for (var page = 1; page <= Math.Max(1, settings.MaxPages); page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageUrl = page == 1 ? searchUrl : AppendPage(searchUrl, page);
            if (page > 1)
                await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);

            try
            {
                var html = await ScraperHttp.FetchHtmlAsync(http, settings, pageUrl, BaseUrl, ct);
                var links = PropertyLinkRegex().Matches(html)
                    .Select(match => match.Groups[1].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (links.Count == 0 && page > 1)
                    break;

                foreach (var link in links)
                {
                    if (stubs.ContainsKey(link))
                        continue;

                    var slug = SlugRegex().Match(link).Groups[1].Value.Replace('-', ' ');
                    stubs[link] = new SourceFeedAdvert
                    {
                        ExternalId = SlugRegex().Match(link).Groups[1].Value,
                        ExternalUrl = link,
                        Title = slug,
                        Description = slug,
                        Category = "immobilier",
                        Location = new FeedLocation { City = "Kinshasa", Formatted = ExtractLocation(slug) ?? "Kinshasa" },
                        Modality = InferModality(slug, slug),
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Zwandako search page failed ({pageUrl}): {ex.Message}");
                break;
            }
        }
    }

    private static SourceFeedAdvert EnrichFromDetailPage(SourceFeedAdvert stub, string html)
    {
        var jsonLd = HtmlScrapeHelpers.TryParseJsonLd(html);
        if (jsonLd is null)
            return stub;

        var root = jsonLd.Value;
        var title = HtmlScrapeHelpers.JsonString(root, "name") ?? stub.Title;
        var description = HtmlScrapeHelpers.JsonString(root, "description") ?? stub.Description;
        var images = root.TryGetProperty("image", out var imageNode)
            ? imageNode.ValueKind switch
            {
                JsonValueKind.Array => imageNode.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList(),
                JsonValueKind.String => [imageNode.GetString()!],
                _ => stub.Images?.ToList() ?? [],
            }
            : stub.Images?.ToList() ?? [];

        FeedPrice? price = null;
        string? currency = null;
        decimal? amount = null;
        if (root.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Object)
        {
            currency = HtmlScrapeHelpers.JsonString(offers, "priceCurrency");
            amount = HtmlScrapeHelpers.JsonDecimal(offers, "price");
            price = new FeedPrice
            {
                Amount = amount,
                Currency = currency,
                Formatted = amount is null ? null : $"{amount} {currency}".Trim(),
                Negotiable = description?.Contains("nego", StringComparison.OrdinalIgnoreCase) == true,
            };
        }

        if (price is null)
            price = HtmlScrapeHelpers.ParsePrice(description);

        var commune = root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object
            ? HtmlScrapeHelpers.JsonString(address, "addressLocality")
            : ExtractLocation(title);

        int? area = null;
        if (root.TryGetProperty("floorSize", out var floorSize) && floorSize.ValueKind == JsonValueKind.Object)
            area = (int?)HtmlScrapeHelpers.JsonDecimal(floorSize, "value");

        var modality = InferModality(title, description);
        var phone = HtmlScrapeHelpers.ExtractPhone(html);

        stub.Title = title;
        stub.Description = description;
        stub.Summary = description;
        stub.Price = price;
        stub.Images = images.Take(10).ToList();
        stub.Location = new FeedLocation
        {
            City = "Kinshasa",
            Commune = commune,
            Formatted = commune is null ? "Kinshasa" : $"{commune}, Kinshasa",
        };
        stub.Details = new FeedDetails
        {
            Area = area,
            PropertyType = InferPropertyType(title, description),
        };
        stub.Contact = phone is null
            ? stub.Contact
            : new FeedContact
            {
                Phone = phone,
                WhatsApp = phone,
                PreferredContact = phone.StartsWith("+243") ? "whatsapp" : "phone",
                IsPubliclyListed = true,
            };
        stub.Modality = modality;
        stub.Subcategory = MapSubcategory(title, modality);
        stub.Tags = BuildTags(title, modality);

        return stub;
    }

    private static string AppendPage(string url, int page)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}page={page}";
    }

    private static string? ExtractFirstImage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var match = ImageSrcRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        return HtmlTagRegex().Replace(html, " ").Trim();
    }

    private static string InferModality(string? title, string? description)
    {
        var text = $"{title} {description}";
        if (text.Contains("louer", StringComparison.OrdinalIgnoreCase) || text.Contains("location", StringComparison.OrdinalIgnoreCase))
            return "rent";
        if (text.Contains("vendre", StringComparison.OrdinalIgnoreCase) || text.Contains("vente", StringComparison.OrdinalIgnoreCase))
            return "sale";
        return "rent";
    }

    private static string? ExtractLocation(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var communes = new[]
        {
            "Gombe", "Ngaliema", "Limete", "Bandalungwa", "Bandal", "Kalamu", "Macampagne",
            "Masina", "Mont Ngafula", "Ndjili", "Kimbanseke", "Maluku", "Selembao",
        };

        return communes.FirstOrDefault(c => title.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private static string? InferPropertyType(string? title, string? description)
    {
        var text = $"{title} {description}".ToLowerInvariant();
        if (text.Contains("appartement")) return "Appartement";
        if (text.Contains("parcelle") || text.Contains("terrain")) return "Parcelle";
        if (text.Contains("maison")) return "Maison";
        if (text.Contains("villa")) return "Villa";
        return null;
    }

    private static string? MapSubcategory(string? title, string modality)
    {
        var type = InferPropertyType(title, null)?.ToLowerInvariant();
        if (type?.Contains("appartement") == true)
            return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
        if (type?.Contains("parcelle") == true || type?.Contains("terrain") == true)
            return "parcelle";
        if (type?.Contains("maison") == true || type?.Contains("villa") == true)
            return modality == "rent" ? "maison_a_louer" : "maison_a_vendre";
        return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
    }

    private static List<string> BuildTags(string? title, string modality)
    {
        var tags = new List<string> { modality == "rent" ? "location" : "vente" };
        var type = InferPropertyType(title, null);
        if (!string.IsNullOrWhiteSpace(type))
            tags.Add(type);
        return tags;
    }

    [GeneratedRegex(@"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSrcRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"/immobilier/([^/]+)/?", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"\?p=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PostIdRegex();

    [GeneratedRegex("""href="(https://zwandako\.com/immobilier/[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PropertyLinkRegex();
}
