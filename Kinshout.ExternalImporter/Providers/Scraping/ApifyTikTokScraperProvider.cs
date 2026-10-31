using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Pulls Kinshasa/Congo TikTok videos via Apify (embed URL kept — do not rehost).
/// </summary>
public sealed class ApifyTikTokScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "clockworks/tiktok-scraper";

    public string Name => settings.Name;

    public async Task<DiscussionFetchResult> FetchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApifyActorId))
            settings.ApifyActorId = DefaultActorId;

        var client = new ApifyClient(http, settings);
        var input = BuildInput();
        using var doc = await client.RunActorAndGetDatasetAsync(input, ct);

        var discussions = new List<SourceFeedDiscussion>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  Apify TikTok: unexpected dataset format.");
            return DiscussionFetchResult.From(discussions, seenIds);
        }

        var rawCount = doc.RootElement.GetArrayLength();
        var filteredOut = 0;
        var minEngagement = ResolveMinEngagement();
        var minPostedAt = ResolveMinPostedAt();
        var maxItems = settings.ResultsLimit > 0 ? settings.ResultsLimit : 15;
        var kept = 0;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (kept >= maxItems)
                break;

            if (item.TryGetProperty("error", out _))
            {
                filteredOut++;
                continue;
            }

            var rawId = ReadString(item, "id")
                ?? ReadString(item, "videoId")
                ?? ReadString(item, "awemeId");
            if (!string.IsNullOrWhiteSpace(rawId))
                seenIds.Add(rawId);

            try
            {
                var mapped = MapVideo(item, minEngagement, minPostedAt);
                if (mapped is not null)
                {
                    discussions.Add(mapped);
                    kept++;
                }
                else
                {
                    filteredOut++;
                }
            }
            catch (Exception ex)
            {
                filteredOut++;
                Console.WriteLine($"  Apify TikTok parse skipped: {ex.Message}");
            }
        }

        if (rawCount > 0)
        {
            Console.WriteLine(
                $"  Apify TikTok: kept {discussions.Count}/{rawCount} Kinshasa videos ({filteredOut} filtered out).");
        }

        var deduped = discussions
            .GroupBy(d => d.ExternalId ?? d.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return DiscussionFetchResult.From(deduped, seenIds);
    }

    private object BuildInput()
    {
        var queries = settings.SearchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim().TrimStart('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queries.Count == 0)
            queries.Add("Kinshasa");

        var resultsPerPage = settings.ResultsLimit > 0 ? settings.ResultsLimit : 15;

        // clockworks/tiktok-scraper accepts hashtags and/or searchQueries depending on version.
        return new
        {
            hashtags = queries,
            searchQueries = queries,
            resultsPerPage,
            resultsLimit = resultsPerPage,
            shouldDownloadVideos = false,
            shouldDownloadCovers = false,
            shouldDownloadSubtitles = false,
            shouldDownloadSlideshowImages = false,
            proxyConfiguration = new { useApifyProxy = true },
        };
    }

    private SourceFeedDiscussion? MapVideo(JsonElement item, int minEngagement, DateTime minPostedAt)
    {
        var id = ReadString(item, "id")
            ?? ReadString(item, "videoId")
            ?? ReadString(item, "awemeId");
        var webUrl = ReadString(item, "webVideoUrl")
            ?? ReadString(item, "url")
            ?? ReadString(item, "shareUrl")
            ?? ReadNestedString(item, "videoMeta", "url");
        var text = ReadString(item, "text")
            ?? ReadString(item, "desc")
            ?? ReadString(item, "title")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(webUrl))
            return null;

        var watchUrl = NormalizeWatchUrl(webUrl, id);
        if (string.IsNullOrWhiteSpace(watchUrl))
            return null;

        var externalId = !string.IsNullOrWhiteSpace(id)
            ? id.Trim()
            : ExtractIdFromUrl(watchUrl) ?? watchUrl;

        var author = ReadNestedString(item, "authorMeta", "name")
            ?? ReadNestedString(item, "authorMeta", "nickName")
            ?? ReadString(item, "author")
            ?? ReadString(item, "nickname");

        var title = BuildTitle(text, author);
        var body = BuildBody(text, watchUrl);

        if (!KinshasaSocialPostFilter.IsKinshasaRelevant(null, title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, body))
            return null;

        var plays = ReadInt(item, "playCount")
            ?? ReadNestedInt(item, "videoMeta", "playCount")
            ?? 0;
        var likes = ReadInt(item, "diggCount")
            ?? ReadInt(item, "likes")
            ?? ReadInt(item, "likeCount")
            ?? 0;
        var comments = ReadInt(item, "commentCount") ?? 0;
        var shares = ReadInt(item, "shareCount") ?? 0;
        var score = plays > 0 ? plays : likes * 20 + comments * 5 + shares * 10;
        if (score < minEngagement)
            return null;

        var postedAt = ReadTimestamp(item, "createTimeISO")
            ?? ReadTimestamp(item, "createTime")
            ?? ReadTimestamp(item, "uploadedAt");
        if (postedAt is { } published && published.ToUniversalTime() < minPostedAt)
            return null;

        // Do not attach TikTok cover/poster images — the feed embeds the video URL only.
        return new SourceFeedDiscussion
        {
            ExternalId = externalId,
            ExternalUrl = watchUrl,
            Title = title,
            Body = body,
            OriginalAuthor = author,
            PublishedAt = postedAt,
            EngagementScore = score,
            Status = "active",
            ImageUrls = [],
        };
    }

    private static string BuildTitle(string text, string? author)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.IsNullOrWhiteSpace(author) ? "TikTok Kinshasa" : $"TikTok · {author}";

        return cleaned.Length <= 120 ? cleaned : cleaned[..117] + "...";
    }

    private static string BuildBody(string text, string watchUrl)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (cleaned.Length > 500)
            cleaned = cleaned[..497] + "...";

        if (string.IsNullOrWhiteSpace(cleaned))
            return watchUrl;

        return $"{cleaned}\n\n{watchUrl}";
    }

    private static string? NormalizeWatchUrl(string? rawUrl, string? videoId)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl))
        {
            var cleaned = rawUrl.Trim().Trim('<', '>');
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out _))
                return cleaned.Split('?', 2)[0].TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(videoId))
            return $"https://www.tiktok.com/@kinoiserie/video/{videoId.Trim()}";

        return null;
    }

    private static string? ExtractIdFromUrl(string url)
    {
        var match = Regex.Match(url, @"/video/(\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private int ResolveMaxAgeDays() => settings.MaxAdvertAgeDays > 0 ? settings.MaxAdvertAgeDays : 7;

    private DateTime ResolveMinPostedAt() =>
        settings.SincePublishedAt?.ToUniversalTime()
        ?? DateTime.UtcNow.AddDays(-ResolveMaxAgeDays());

    private int ResolveMinEngagement() => settings.MinEngagementScore > 0 ? settings.MinEngagementScore : 1000;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, string parent, string child)
    {
        if (!element.TryGetProperty(parent, out var node) || node.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(node, child);
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.Number when value.TryGetInt64(out var l) => l > int.MaxValue ? int.MaxValue : (int)l,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? ReadNestedInt(JsonElement element, string parent, string child)
    {
        if (!element.TryGetProperty(parent, out var node) || node.ValueKind != JsonValueKind.Object)
            return null;
        return ReadInt(node, child);
    }

    private static DateTime? ReadTimestamp(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
        {
            // TikTok often sends seconds; treat large values as ms.
            return unix > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }

        if (value.ValueKind == JsonValueKind.String)
            return HtmlScrapeHelpers.ParseLooseDate(value.GetString());

        return null;
    }
}
