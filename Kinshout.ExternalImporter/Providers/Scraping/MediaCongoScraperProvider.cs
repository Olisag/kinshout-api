using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Import;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed partial class MediaCongoScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    private const string BaseUrl = "https://www.mediacongo.net/";

    public string Name => settings.Name;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken ct)
    {
        var listingUrls = await CollectListingUrlsAsync(ct);
        var seenIds = listingUrls
            .Select(url => ListingIdRegex().Match(url).Groups[1].Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adverts = new List<SourceFeedAdvert>();

        foreach (var listingUrl in listingUrls)
        {
            ct.ThrowIfCancellationRequested();

            var externalId = ListingIdRegex().Match(listingUrl).Groups[1].Value;
            if (ImportAdvertKeys.IsKnown(settings.KnownAdvertKeys, settings.Provider, externalId))
                continue;

            if (!settings.FetchDetails)
            {
                adverts.Add(CreateStubFromListingUrl(listingUrl));
                continue;
            }

            try
            {
                await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);
                var html = await ScraperHttp.FetchHtmlAsync(http, settings, listingUrl, BaseUrl, ct);
                var parsed = ParseDetailPage(html, listingUrl);
                if (parsed is not null)
                    adverts.Add(parsed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  MediaCongo detail failed for {listingUrl}: {ex.Message}");
            }
        }

        var deduped = adverts
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalUrl) && !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return ProviderFetchResult.From(deduped, seenIds);
    }

    private async Task<List<string>> CollectListingUrlsAsync(CancellationToken ct)
    {
        var seeds = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.RecentUrl))
            seeds.Add(settings.RecentUrl);
        if (!string.IsNullOrWhiteSpace(settings.PopularUrl))
            seeds.Add(settings.PopularUrl);
        seeds.AddRange(settings.ExtraListingUrls.Where(url => !string.IsNullOrWhiteSpace(url)));

        if (seeds.Count == 0)
            seeds.Add($"{BaseUrl}categories-cat-114_immobilier_vente_location-page-1.html");

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var maxPages = SupportsPagination(seed) ? Math.Max(1, settings.MaxPages) : 1;

            for (var page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var pageUrl = BuildPageUrl(seed, page);
                if (page > 1)
                    await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);

                try
                {
                    var html = await ScraperHttp.FetchHtmlAsync(http, settings, pageUrl, BaseUrl, ct);
                    var found = ListingLinkRegex().Matches(html)
                        .Select(match => AbsoluteListingUrl(match.Value))
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .ToList();

                    if (found.Count == 0 && page > 1)
                        break;

                    foreach (var url in found)
                        urls.Add(url);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  MediaCongo listing page failed ({pageUrl}): {ex.Message}");
                    if (page == 1)
                        break;
                }
            }
        }

        return urls.ToList();
    }

    private static bool SupportsPagination(string seed) =>
        seed.Contains("categories-cat-", StringComparison.OrdinalIgnoreCase)
        || PageSuffixRegex().IsMatch(seed);

    private static string BuildPageUrl(string seed, int page)
    {
        if (page <= 1)
            return seed;

        if (PageSuffixRegex().IsMatch(seed))
            return PageSuffixRegex().Replace(seed, $"-page-{page}.html");

        if (seed.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return seed.Replace(".html", $"-page-{page}.html", StringComparison.OrdinalIgnoreCase);

        return seed.TrimEnd('/') + $"/page/{page}";
    }

    private static string AbsoluteListingUrl(string href) =>
        HtmlScrapeHelpers.AbsoluteUrl(BaseUrl, href);

    private SourceFeedAdvert? ParseDetailPage(string html, string url)
    {
        var doc = HtmlScrapeHelpers.Load(html);
        var externalId = ListingIdRegex().Match(url).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var title = CleanTitle(doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var priceNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'annonceprix')]")
            ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'ah_price')]");
        var price = HtmlScrapeHelpers.ParsePrice(HtmlScrapeHelpers.Text(priceNode))
            ?? HtmlScrapeHelpers.ParsePrice(title);

        var descriptionNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'annonce_description')]");
        var description = HtmlScrapeHelpers.Text(descriptionNode);

        var dataPairs = doc.DocumentNode
            .SelectNodes("//*[contains(@class,'annonce_data_type')]")
            ?.Select(typeNode =>
            {
                var label = HtmlScrapeHelpers.Text(typeNode);
                var valueNode = typeNode.SelectSingleNode("following-sibling::*[contains(@class,'annonce_data_value')][1]");
                var value = HtmlScrapeHelpers.Text(valueNode);
                return (label, value);
            })
            .Where(pair => !string.IsNullOrWhiteSpace(pair.label))
            .ToDictionary(pair => pair.label!, pair => pair.value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var galleryImages = doc.DocumentNode
            .SelectNodes("//*[contains(@class,'annonce_gallery')]//img[@src]")
            ?.Select(node => HtmlScrapeHelpers.AbsoluteUrl(url, node.GetAttributeValue("src", "")))
            .Where(src => !string.IsNullOrWhiteSpace(src))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (galleryImages.Count == 0)
        {
            galleryImages = doc.DocumentNode
                .SelectNodes("//img[contains(@src,'dpics/ads/') or contains(@src,'/cache/')]")
                ?.Select(node => HtmlScrapeHelpers.AbsoluteUrl(url, node.GetAttributeValue("src", "")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }

        var propertyType = dataPairs.GetValueOrDefault("Type :") ?? dataPairs.GetValueOrDefault("Type de bien :");
        var bedrooms = ParseInt(dataPairs.GetValueOrDefault("Nombre de chambres :"));
        var area = ParseInt(dataPairs.GetValueOrDefault("Superficie en m² :"));
        var category = ListingCategoryMapper.FromMediaCongo(url, title, description, settings.DefaultCategory);

        return new SourceFeedAdvert
        {
            ExternalId = externalId,
            ExternalUrl = url,
            Title = title,
            Description = description ?? title,
            Summary = description,
            Category = category.Category,
            Subcategory = category.Subcategory ?? MapSubcategory(propertyType, category.Modality),
            Status = "active",
            PublishedAt = HtmlScrapeHelpers.ParseLooseDate(FindDate(dataPairs)),
            Price = price,
            Location = new FeedLocation
            {
                City = "Kinshasa",
                Commune = ExtractCommune(title, dataPairs),
                Formatted = ExtractCommune(title, dataPairs) is { } commune ? $"{commune}, Kinshasa" : "Kinshasa",
            },
            Details = new FeedDetails
            {
                Bedrooms = bedrooms,
                Area = area,
                PropertyType = propertyType,
            },
            Images = galleryImages.Take(10).ToList(),
            Contact = new FeedContact
            {
                Phone = HtmlScrapeHelpers.ExtractPhone(html),
                WhatsApp = HtmlScrapeHelpers.ExtractPhone(html),
                PreferredContact = "phone",
                IsPubliclyListed = true,
            },
            Modality = category.Modality,
            Tags = BuildTags(propertyType, category.Modality, dataPairs),
        };
    }

    private SourceFeedAdvert CreateStubFromListingUrl(string url)
    {
        var externalId = ListingIdRegex().Match(url).Groups[1].Value;
        var slugTitle = SlugTitleRegex().Match(url).Groups[1].Value.Replace('_', ' ');
        var category = ListingCategoryMapper.FromMediaCongo(url, slugTitle, slugTitle, settings.DefaultCategory);
        return new SourceFeedAdvert
        {
            ExternalId = externalId,
            ExternalUrl = url,
            Title = slugTitle,
            Description = slugTitle,
            Category = category.Category,
            Subcategory = category.Subcategory,
            Location = new FeedLocation { City = "Kinshasa", Formatted = "Kinshasa" },
            Modality = category.Modality,
        };
    }

    private static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var title = WebUtility.HtmlDecode(raw).Trim();
        title = title.Split('-')[0].Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static string InferModality(IReadOnlyDictionary<string, string?> data, string title)
    {
        var offer = data.GetValueOrDefault("Offre :") ?? data.GetValueOrDefault("Type d'offre :");
        if (!string.IsNullOrWhiteSpace(offer))
        {
            if (offer.Contains("location", StringComparison.OrdinalIgnoreCase) || offer.Contains("louer", StringComparison.OrdinalIgnoreCase))
                return "rent";
            if (offer.Contains("vente", StringComparison.OrdinalIgnoreCase))
                return "sale";
        }

        return title.Contains("louer", StringComparison.OrdinalIgnoreCase) ? "rent" : "sale";
    }

    private static string? MapSubcategory(string? propertyType, string modality)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
            return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";

        var type = propertyType.ToLowerInvariant();
        if (type.Contains("appartement"))
            return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
        if (type.Contains("parcelle") || type.Contains("terrain"))
            return "parcelle";
        if (type.Contains("maison") || type.Contains("villa"))
            return modality == "rent" ? "maison_a_louer" : "maison_a_vendre";

        return null;
    }

    private static string? ExtractCommune(string title, IReadOnlyDictionary<string, string?> data)
    {
        foreach (var value in data.Values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (value!.Contains("gombe", StringComparison.OrdinalIgnoreCase)) return "Gombe";
            if (value.Contains("ngaliema", StringComparison.OrdinalIgnoreCase)) return "Ngaliema";
            if (value.Contains("limete", StringComparison.OrdinalIgnoreCase)) return "Limete";
            if (value.Contains("bandal", StringComparison.OrdinalIgnoreCase)) return "Bandalungwa";
            if (value.Contains("kalamu", StringComparison.OrdinalIgnoreCase)) return "Kalamu";
        }

        var communes = new[] { "Gombe", "Ngaliema", "Limete", "Bandal", "Kalamu", "Masina", "Kinshasa" };
        return communes.FirstOrDefault(c => title.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindDate(IReadOnlyDictionary<string, string?> data)
    {
        foreach (var key in data.Keys)
        {
            if (key.Contains("date", StringComparison.OrdinalIgnoreCase) || key.Contains("publi", StringComparison.OrdinalIgnoreCase))
                return data[key];
        }

        return null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = Regex.Replace(value, @"[^\d]", "");
        return int.TryParse(digits, out var parsed) ? parsed : null;
    }

    private static List<string> BuildTags(string? propertyType, string modality, IReadOnlyDictionary<string, string?> data)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(propertyType))
            tags.Add(propertyType);
        tags.Add(modality == "rent" ? "location" : "vente");
        if (data.TryGetValue("Offre :", out var offer) && !string.IsNullOrWhiteSpace(offer))
            tags.Add(offer!);
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    [GeneratedRegex("annonce-mediacongo-\\d+(?:_[^\"']+)?\\.html", RegexOptions.IgnoreCase)]
    private static partial Regex ListingLinkRegex();

    [GeneratedRegex("annonce-mediacongo-(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListingIdRegex();

    [GeneratedRegex("-page-\\d+\\.html$", RegexOptions.IgnoreCase)]
    private static partial Regex PageSuffixRegex();

    [GeneratedRegex("annonce-mediacongo-\\d+_[^_]+_[^_]+_(.+)\\.html", RegexOptions.IgnoreCase)]
    private static partial Regex SlugTitleRegex();
}
