using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed partial class JijiRdcScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    public string Name => settings.Name;

    public async Task<IReadOnlyList<SourceFeedAdvert>> FetchAsync(CancellationToken ct)
    {
        var listingUrl = settings.RecentUrl ?? "https://jiji.cd/kinshasa/immobilier";
        ValidateCookieConfiguration();

        string html;

        try
        {
            html = await ScraperHttp.FetchHtmlAsync(http, settings, listingUrl, "https://jiji.cd/", ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Jiji RDC blocked or unreachable ({listingUrl}). {JijiSetupHint()} Original: {ex.Message}",
                ex);
        }

        if (IsCloudflareChallenge(html))
        {
            throw new InvalidOperationException(
                $"Jiji RDC returned a Cloudflare challenge. {JijiSetupHint()}");
        }

        var adverts = ParseListingHtml(html, listingUrl);
        if (adverts.Count == 0)
            adverts = ParseEmbeddedJson(html, listingUrl);

        if (adverts.Count == 0)
            Console.WriteLine("  Jiji RDC: no listings parsed. The page layout may have changed or content is JS-only.");

        if (!settings.FetchDetails)
            return adverts;

        var enriched = new List<SourceFeedAdvert>();
        foreach (var stub in adverts)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(stub.ExternalUrl))
            {
                enriched.Add(stub);
                continue;
            }

            try
            {
                await ScraperHttp.DelayBetweenRequestsAsync(settings, ct);
                var detailHtml = await ScraperHttp.FetchHtmlAsync(http, settings, stub.ExternalUrl, listingUrl, ct);
                enriched.Add(ParseDetailHtml(stub, detailHtml));
            }
            catch
            {
                enriched.Add(stub);
            }
        }

        return enriched
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<SourceFeedAdvert> ParseListingHtml(string html, string baseUrl)
    {
        var adverts = new List<SourceFeedAdvert>();
        foreach (Match match in ListingLinkRegex().Matches(html))
        {
            var url = HtmlScrapeHelpers.AbsoluteUrl(baseUrl, match.Groups[1].Value);
            var title = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            adverts.Add(new SourceFeedAdvert
            {
                ExternalId = ListingIdRegex().Match(url).Groups[1].Value,
                ExternalUrl = url,
                Title = title,
                Description = title,
                Category = "immobilier",
                Location = new FeedLocation { City = "Kinshasa", Formatted = "Kinshasa" },
                Modality = title.Contains("rent", StringComparison.OrdinalIgnoreCase) || title.Contains("louer", StringComparison.OrdinalIgnoreCase)
                    ? "rent"
                    : "sale",
            });
        }

        return adverts;
    }

    private static List<SourceFeedAdvert> ParseEmbeddedJson(string html, string baseUrl)
    {
        var adverts = new List<SourceFeedAdvert>();
        var match = NextDataRegex().Match(html);
        if (!match.Success)
            return adverts;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            WalkJsonForListings(doc.RootElement, baseUrl, adverts);
        }
        catch
        {
            // ignore malformed embedded payloads
        }

        return adverts;
    }

    private static void WalkJsonForListings(JsonElement node, string baseUrl, List<SourceFeedAdvert> adverts)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("title", out var titleNode)
                && titleNode.ValueKind == JsonValueKind.String
                && node.TryGetProperty("url", out var urlNode)
                && urlNode.ValueKind == JsonValueKind.String)
            {
                var url = HtmlScrapeHelpers.AbsoluteUrl(baseUrl, urlNode.GetString());
                var title = titleNode.GetString();
                adverts.Add(new SourceFeedAdvert
                {
                    ExternalId = ListingIdRegex().Match(url).Groups[1].Value,
                    ExternalUrl = url,
                    Title = title,
                    Description = title,
                    Category = "immobilier",
                    Location = new FeedLocation { City = "Kinshasa", Formatted = "Kinshasa" },
                });
            }

            foreach (var property in node.EnumerateObject())
                WalkJsonForListings(property.Value, baseUrl, adverts);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkJsonForListings(item, baseUrl, adverts);
        }
    }

    private static SourceFeedAdvert ParseDetailHtml(SourceFeedAdvert stub, string html)
    {
        var jsonLd = HtmlScrapeHelpers.TryParseJsonLd(html);
        if (jsonLd is null)
            return stub;

        var root = jsonLd.Value;
        var description = HtmlScrapeHelpers.JsonString(root, "description") ?? stub.Description;
        var priceText = root.TryGetProperty("offers", out var offers)
            ? $"{HtmlScrapeHelpers.JsonDecimal(offers, "price")} {HtmlScrapeHelpers.JsonString(offers, "priceCurrency")}".Trim()
            : null;

        stub.Description = description;
        stub.Summary = description;
        stub.Price = HtmlScrapeHelpers.ParsePrice(priceText) ?? HtmlScrapeHelpers.ParsePrice(description);
        if (root.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String)
            stub.Images = [image.GetString()!];
        stub.Contact = new FeedContact
        {
            Phone = HtmlScrapeHelpers.ExtractPhone(html),
            WhatsApp = HtmlScrapeHelpers.ExtractPhone(html),
            IsPubliclyListed = true,
        };

        return stub;
    }

    private void ValidateCookieConfiguration()
    {
        if (!settings.Headers.TryGetValue("Cookie", out var cookie)
            || string.IsNullOrWhiteSpace(cookie)
            || cookie.Contains("${JIJI_COOKIE}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(JijiSetupHint());
        }

        if (!cookie.Contains("cf_clearance", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  Jiji RDC warning: Cookie is set but missing cf_clearance — Cloudflare may still block requests.");
        }
    }

    private static bool IsCloudflareChallenge(string html) =>
        html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase);

    private static string JijiSetupHint() =>
        """
        Jiji.cd is protected by Cloudflare. To enable the scraper:
        1. Open https://jiji.cd/kinshasa/immobilier in Chrome and complete any Cloudflare check.
        2. DevTools → Network → reload the page → click the document request → Headers → copy the full Cookie value.
        3. Add to repo-root .env: JIJI_COOKIE="paste-cookie-here"
        4. Re-run: dotnet run -- --once --dry-run
        Cookies expire every few days; refresh JIJI_COOKIE when imports start failing.
        Apify's Jiji actors do not support jiji.cd yet (only jiji.ng, .co.ke, etc.).
        """;

    [GeneratedRegex("""href="(/kinshasa/[^"]+)"[^>]*>([^<]{4,})""", RegexOptions.IgnoreCase)]
    private static partial Regex ListingLinkRegex();

    [GeneratedRegex(@"/(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ListingIdRegex();

    [GeneratedRegex(@"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();
}
