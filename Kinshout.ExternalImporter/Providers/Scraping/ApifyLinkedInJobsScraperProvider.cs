using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed class ApifyLinkedInJobsScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    private const string DefaultGeoId = "107853273";
    private const string DefaultActorId = "curious_coder/linkedin-jobs-scraper";
    private const string PastMonthTimeFilter = "r2592000";

    public string Name => settings.Name;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApifyActorId))
            settings.ApifyActorId = DefaultActorId;

        var client = new ApifyClient(http, settings);
        var input = BuildInput();
        using var doc = await client.RunActorAndGetDatasetAsync(input, ct);

        var adverts = new List<SourceFeedAdvert>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  Apify LinkedIn Jobs: unexpected dataset format.");
            return ProviderFetchResult.From(adverts, seenIds);
        }

        var rawCount = doc.RootElement.GetArrayLength();
        var filteredOut = 0;
        var maxAgeDays = ResolveMaxAdvertAgeDays();
        var minPostedAt = DateTime.UtcNow.AddDays(-maxAgeDays);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var rawId = ReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(rawId))
                seenIds.Add(rawId);

            try
            {
                var advert = MapJob(item, minPostedAt);
                if (advert is not null)
                    adverts.Add(advert);
                else
                    filteredOut++;
            }
            catch (Exception ex)
            {
                filteredOut++;
                Console.WriteLine($"  Apify LinkedIn job parse skipped: {ex.Message}");
            }
        }

        if (rawCount > 0)
        {
            Console.WriteLine(
                $"  Apify LinkedIn Jobs: kept {adverts.Count}/{rawCount} Kinshasa DRC listings ({filteredOut} filtered out, maxAgeDays={maxAgeDays}).");
        }

        var deduped = adverts
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return ProviderFetchResult.From(deduped, seenIds);
    }

    private object BuildInput()
    {
        var searchUrl = BuildSearchUrl();
        var count = settings.ResultsLimit > 0
            ? settings.ResultsLimit
            : Math.Max(10, settings.MaxPages * 25);

        return new
        {
            urls = new[] { searchUrl },
            count,
            scrapeCompany = settings.FetchDetails,
        };
    }

    private string BuildSearchUrl()
    {
        foreach (var url in CollectConfiguredUrls())
        {
            if (!string.IsNullOrWhiteSpace(url))
                return EnsureTimeFilter(url);
        }

        var geoId = ResolveGeoId();
        var city = string.IsNullOrWhiteSpace(settings.DefaultCity) ? "Kinshasa" : settings.DefaultCity.Trim();
        var location = Uri.EscapeDataString($"{city}, Democratic Republic of the Congo");
        return $"https://www.linkedin.com/jobs/search/?geoId={geoId}&location={location}&f_TPR={PastMonthTimeFilter}";
    }

    private IEnumerable<string?> CollectConfiguredUrls()
    {
        if (!string.IsNullOrWhiteSpace(settings.RecentUrl)
            && settings.RecentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            yield return settings.RecentUrl;
        }

        foreach (var url in settings.ExtraListingUrls.Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            yield return url;
    }

    private string EnsureTimeFilter(string url)
    {
        if (url.Contains("f_TPR=", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}f_TPR={PastMonthTimeFilter}";
    }

    private string ResolveGeoId()
    {
        if (!string.IsNullOrWhiteSpace(settings.LinkedInGeoId))
            return settings.LinkedInGeoId.Trim();

        if (!string.IsNullOrWhiteSpace(settings.MarketplaceLocation)
            && settings.MarketplaceLocation.All(char.IsDigit))
        {
            return settings.MarketplaceLocation.Trim();
        }

        return DefaultGeoId;
    }

    private int ResolveMaxAdvertAgeDays() =>
        settings.MaxAdvertAgeDays > 0 ? settings.MaxAdvertAgeDays : 30;

    private SourceFeedAdvert? MapJob(JsonElement item, DateTime minPostedAt)
    {
        if (item.TryGetProperty("error", out _) || item.TryGetProperty("errorInfo", out _))
            return null;

        var id = ReadString(item, "id");
        var title = ReadString(item, "title");
        var link = ReadString(item, "link") ?? ReadString(item, "url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
            return null;

        var location = ReadString(item, "location");
        var descriptionText = ReadString(item, "descriptionText");
        var descriptionHtml = ReadString(item, "descriptionHtml");
        var description = BuildDescription(ReadString(item, "companyName"), descriptionText, descriptionHtml, title);

        if (IsExpiredJob(item, title, description, descriptionHtml))
            return null;

        if (!DrcLinkedInJobFilter.IsDrcKinshasaJob(location, title, description))
            return null;

        var postedAt = ReadPostedAt(item);
        if (postedAt is { } published && published.ToUniversalTime() < minPostedAt)
            return null;

        var tags = new List<string> { "linkedin", "apify", "emploi" };
        var companyName = ReadString(item, "companyName");
        if (!string.IsNullOrWhiteSpace(companyName))
            tags.Add(companyName.Trim());

        foreach (var tag in new[] { ReadString(item, "employmentType"), ReadString(item, "seniorityLevel") })
        {
            if (!string.IsNullOrWhiteSpace(tag))
                tags.Add(tag.Trim());
        }

        FeedPrice? price = null;
        if (item.TryGetProperty("salary", out var salaryNode) && salaryNode.ValueKind == JsonValueKind.String)
        {
            var salaryText = salaryNode.GetString();
            if (!string.IsNullOrWhiteSpace(salaryText))
            {
                price = HtmlScrapeHelpers.ParsePrice(salaryText)
                    ?? new FeedPrice { Formatted = salaryText.Trim() };
            }
        }

        var images = new List<string>();
        var logo = ReadString(item, "companyLogo");
        if (!string.IsNullOrWhiteSpace(logo))
            images.Add(logo);

        return new SourceFeedAdvert
        {
            ExternalId = id,
            ExternalUrl = link,
            Title = title,
            Description = description,
            Summary = TruncateSummary(description),
            Category = "emploi_services",
            Subcategory = "offre_emploi",
            Status = "active",
            PublishedAt = postedAt,
            Price = price,
            Location = new FeedLocation
            {
                City = settings.DefaultCity,
                Commune = KinshasaListingFilter.ExtractCommune(location ?? title),
                Formatted = string.IsNullOrWhiteSpace(location) ? settings.DefaultCity : location,
            },
            Images = images,
            Intent = ["offre"],
            Modality = "offre",
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
        };
    }

    private static bool IsExpiredJob(JsonElement item, string? title, string? description, string? descriptionHtml)
    {
        if (item.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.True)
            return true;

        if (item.TryGetProperty("isExpired", out expired) && expired.ValueKind == JsonValueKind.True)
            return true;

        var blob = string.Join(" ", new[] { title, description, descriptionHtml }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return blob.Contains("no longer accepting applications", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("n'accepte plus de candidatures", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("offre plus disponible", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDescription(string? companyName, string? descriptionText, string? descriptionHtml, string title)
    {
        var body = !string.IsNullOrWhiteSpace(descriptionText)
            ? descriptionText.Trim()
            : StripHtml(descriptionHtml);

        if (string.IsNullOrWhiteSpace(body))
            body = title;

        if (string.IsNullOrWhiteSpace(companyName))
            return body;

        return $"Entreprise : {companyName.Trim()}\n\n{body}";
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        return System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "))
            .Replace("\u00a0", " ")
            .Trim();
    }

    private static string TruncateSummary(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "";

        var normalized = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim();
        return normalized.Length <= 280 ? normalized : normalized[..277] + "...";
    }

    private static DateTime? ReadPostedAt(JsonElement item)
    {
        var postedAt = ReadString(item, "postedAt");
        if (string.IsNullOrWhiteSpace(postedAt))
            return null;

        if (DateOnly.TryParse(postedAt, out var dateOnly))
            return dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return HtmlScrapeHelpers.ParseLooseDate(postedAt);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
