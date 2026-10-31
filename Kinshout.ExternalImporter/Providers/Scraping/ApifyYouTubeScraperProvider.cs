using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed partial class ApifyYouTubeScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "streamers/youtube-scraper";

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
            Console.WriteLine("  Apify YouTube: unexpected dataset format.");
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

            var rawId = ReadString(item, "id");
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
                Console.WriteLine($"  Apify YouTube parse skipped: {ex.Message}");
            }
        }

        if (rawCount > 0)
        {
            Console.WriteLine(
                $"  Apify YouTube: kept {discussions.Count}/{rawCount} Kinshasa videos ({filteredOut} filtered out).");
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
            .Select(q => q.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queries.Count == 0)
            queries.Add("Kinshasa");

        var maxResults = settings.ResultsLimit > 0 ? settings.ResultsLimit : 15;
        // Prefer recent uploads; actor still returns absolute/relative dates we filter locally.
        var dateFilter = string.IsNullOrWhiteSpace(settings.PopularUrl)
            ? "week"
            : settings.PopularUrl.Trim().ToLowerInvariant();

        return new
        {
            searchQueries = queries,
            maxResults,
            maxResultsShorts = 0,
            maxResultStreams = 0,
            sortingOrder = "date",
            dateFilter,
            videoType = "video",
        };
    }

    private SourceFeedDiscussion? MapVideo(JsonElement item, int minEngagement, DateTime minPostedAt)
    {
        var id = ReadString(item, "id");
        var title = ReadString(item, "title");
        var url = NormalizeWatchUrl(ReadString(item, "url"), id);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            return null;

        var description = ReadString(item, "text")
            ?? ReadString(item, "description")
            ?? string.Empty;
        var author = ReadString(item, "channelName") ?? ReadString(item, "channel");
        var location = ReadString(item, "location") ?? ReadString(item, "channelLocation");
        var body = BuildBody(title, description, url);

        if (!KinshasaSocialPostFilter.IsKinshasaRelevant(location, title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, body))
            return null;

        var views = ReadInt(item, "viewCount") ?? 0;
        var likes = ReadInt(item, "likes") ?? ReadInt(item, "likeCount") ?? 0;
        // Prefer view count as the main quality signal for YouTube.
        var score = views > 0 ? views : likes * 20;
        if (score < minEngagement)
            return null;

        var postedAt = ParseYouTubeDate(ReadString(item, "date"))
            ?? ReadTimestamp(item, "uploadDate")
            ?? ReadTimestamp(item, "publishedAt");
        if (postedAt is { } published && published.ToUniversalTime() < minPostedAt)
            return null;

        return new SourceFeedDiscussion
        {
            ExternalId = id,
            ExternalUrl = url,
            Title = title.Length <= 120 ? title : title[..117] + "...",
            Body = body,
            OriginalAuthor = author,
            PublishedAt = postedAt,
            EngagementScore = score,
            Status = "active",
        };
    }

    private static string BuildBody(string title, string description, string watchUrl)
    {
        var cleanedDescription = Regex.Replace(description ?? string.Empty, @"\s+", " ").Trim();
        if (cleanedDescription.Length > 500)
            cleanedDescription = cleanedDescription[..497] + "...";

        if (string.IsNullOrWhiteSpace(cleanedDescription))
            return $"{title.Trim()}\n\n{watchUrl}";

        return $"{title.Trim()}\n\n{cleanedDescription}\n\n{watchUrl}";
    }

    private static string? NormalizeWatchUrl(string? rawUrl, string? videoId)
    {
        if (!string.IsNullOrWhiteSpace(videoId))
            return $"https://www.youtube.com/watch?v={videoId.Trim()}";

        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;

        var cleaned = rawUrl.Trim().Trim('<', '>');
        return cleaned;
    }

    internal static DateTime? ParseYouTubeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var absolute = HtmlScrapeHelpers.ParseLooseDate(trimmed);
        if (absolute is not null)
            return absolute;

        var relative = RelativeAgoRegex().Match(trimmed);
        if (!relative.Success)
            return null;

        if (!int.TryParse(relative.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
            || amount < 0)
        {
            return null;
        }

        var unit = relative.Groups[2].Value.ToLowerInvariant();
        var now = DateTime.UtcNow;
        return unit switch
        {
            "second" or "seconds" => now.AddSeconds(-amount),
            "minute" or "minutes" => now.AddMinutes(-amount),
            "hour" or "hours" => now.AddHours(-amount),
            "day" or "days" => now.AddDays(-amount),
            "week" or "weeks" => now.AddDays(-7 * amount),
            "month" or "months" => now.AddMonths(-amount),
            "year" or "years" => now.AddYears(-amount),
            _ => null,
        };
    }

    private static DateTime? ReadTimestamp(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return ParseYouTubeDate(value.GetString());

        return null;
    }

    private int ResolveMaxAgeDays() => settings.MaxAdvertAgeDays > 0 ? settings.MaxAdvertAgeDays : 7;

    private DateTime ResolveMinPostedAt() =>
        settings.SincePublishedAt?.ToUniversalTime()
        ?? DateTime.UtcNow.AddDays(-ResolveMaxAgeDays());

    private int ResolveMinEngagement() => settings.MinEngagementScore > 0 ? settings.MinEngagementScore : 500;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    [GeneratedRegex(@"^(\d+)\s+(second|seconds|minute|minutes|hour|hours|day|days|week|weeks|month|months|year|years)\s+ago$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeAgoRegex();
}
