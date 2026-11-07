using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>Daily TikTok search for Congolese jokes, gossip, and everyday clips. Not a list of famous accounts.</summary>
public sealed class ApifyTikTokScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "clockworks/tiktok-scraper";

    public string Name => settings.Name;

    public async Task<DiscussionFetchResult> FetchAsync(CancellationToken ct)
    {
        var queries = settings.SearchQueries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queries.Count == 0)
            queries.Add("Kinshasa");

        if (string.IsNullOrWhiteSpace(settings.ApifyActorId))
            settings.ApifyActorId = DefaultActorId;

        var limit = settings.ResultsLimit > 0 ? settings.ResultsLimit : 16;
        var minScore = settings.MinEngagementScore > 0 ? settings.MinEngagementScore : 2000;
        var client = new ApifyClient(http, settings);
        using var doc = await client.RunActorAndGetDatasetAsync(new
        {
            searchQueries = queries,
            hashtags = queries.Select(query => query.Replace(" ", "", StringComparison.Ordinal)).ToList(),
            resultsPerPage = limit,
            shouldDownloadVideos = false,
            shouldDownloadCovers = false,
        }, ct);

        var discussions = new List<SourceFeedDiscussion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return DiscussionFetchResult.From(discussions, seen);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var url = ReadString(item, "webVideoUrl") ?? ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
                continue;

            if (discussions.Count >= limit)
                break;

            var author = ReadAuthor(item);
            var text = ReadString(item, "text") ?? url;
            var title = TrimTitle(text);
            if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, text)
                || KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, text))
                continue;

            var score = ReadInt(item, "playCount") + (ReadInt(item, "diggCount") * 5);
            if (score < minScore)
                continue;

            seen.Add(id);
            discussions.Add(new SourceFeedDiscussion
            {
                ExternalId = id,
                ExternalUrl = url,
                Title = title,
                Body = text,
                OriginalAuthor = author,
                CommunitySlug = CongoleseSocialTopic.Slug(title, text),
                PublishedAt = ReadPublished(item),
                EngagementScore = score,
                Status = "active",
                VideoUrls = [url],
            });
        }

        Console.WriteLine($"  TikTok: kept {discussions.Count} buzzing clip(s) from {queries.Count} Congolese search(es).");
        return DiscussionFetchResult.From(discussions, seen);
    }

    private static string TrimTitle(string text)
    {
        var line = text.Replace('\n', ' ').Trim();
        return line.Length <= 140 ? line : line[..140].Trim();
    }

    private static string? ReadAuthor(JsonElement item)
    {
        if (item.TryGetProperty("authorMeta", out var meta))
        {
            var name = ReadString(meta, "name") ?? ReadString(meta, "nickName");
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return ReadString(item, "author");
    }

    private static DateTime? ReadPublished(JsonElement item)
    {
        var iso = ReadString(item, "createTimeISO");
        if (DateTime.TryParse(iso, out var parsed))
            return parsed.ToUniversalTime();

        if (item.TryGetProperty("createTime", out var unix) && unix.TryGetInt64(out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        return null;
    }

    private static int ReadInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node))
            return 0;

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var number))
            return number;

        return int.TryParse(node.GetString(), out var parsed) ? parsed : 0;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return null;

        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
