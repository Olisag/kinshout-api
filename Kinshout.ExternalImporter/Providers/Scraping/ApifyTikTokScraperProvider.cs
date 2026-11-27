using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Daily TikTok search for what Congolese people are passing around, plus the
/// watched profiles. A clip stays only when people are reacting to it.
/// </summary>
public sealed class ApifyTikTokScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "clockworks/tiktok-scraper";

    public string Name => settings.Name;

    public async Task<DiscussionFetchResult> FetchAsync(CancellationToken ct)
    {
        var configured = settings.SearchQueries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configured.Count == 0)
            configured.Add("Kinshasa");
        var rotated = settings.DiscoveryRotation
            ? FeedDiscoveryPlanner.TikTokSearches(DateTime.UtcNow)
            : [];
        var queries = rotated.Count > 0
            ? rotated.Select(query => query.Text).ToList()
            : configured;
        if (rotated.Count > 0)
        {
            Console.WriteLine("  TikTok discovery: " + string.Join(" | ", rotated.Select(query => $"{query.Lane}: {query.Text}")));
        }

        if (string.IsNullOrWhiteSpace(settings.ApifyActorId))
            settings.ApifyActorId = DefaultActorId;

        var minScore = settings.MinEngagementScore > 0 ? settings.MinEngagementScore : BuzzGate.Floor;
        var client = new ApifyClient(http, settings);
        var discussions = new List<SourceFeedDiscussion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<DiscoveryCheck>();
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = 0;

        // One search at a time. One run of every query plus made-up hashtags
        // was still going when the importer stopped waiting, so nothing was saved.
        foreach (var query in queries)
        {
            JsonDocument doc;
            try
            {
                doc = await client.RunActorAndGetDatasetAsync(new
                {
                    searchQueries = new[] { query },
                    resultsPerPage = 12,
                    shouldDownloadVideos = false,
                    shouldDownloadCovers = false,
                }, ct);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  TikTok search \"{query}\" failed: {ex.Message}");
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    Collect(doc.RootElement, query, searchEstablishesRelevance: rotated.Count > 0);
            }
        }

        var profiles = settings.DiscoveryRotation
            ? settings.ProfileHandles
                .Select(handle => handle.Trim().TrimStart('@'))
                .Where(handle => handle.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        var profileFailed = 0;
        foreach (var profile in profiles)
        {
            JsonDocument doc;
            try
            {
                doc = await client.RunActorAndGetDatasetAsync(new
                {
                    profiles = new[] { profile },
                    resultsPerPage = 8,
                    shouldDownloadVideos = false,
                    shouldDownloadCovers = false,
                }, ct);
            }
            catch (Exception ex)
            {
                profileFailed++;
                Console.WriteLine($"  TikTok profile @{profile} failed: {ex.Message}");
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    Collect(doc.RootElement, $"@{profile}", searchEstablishesRelevance: true);
            }
        }

        if (discussions.Count == 0 && failed == queries.Count && profileFailed == profiles.Count && queries.Count + profiles.Count > 0)
            throw new InvalidOperationException($"TikTok: all {failed} searches and {profileFailed} profiles failed.");

        var ranked = RankReserve(discussions);
        Console.WriteLine($"  TikTok: kept {ranked.Count} buzzing clip(s) from {queries.Count} search(es) and {profiles.Count} profile(s).");
        return DiscussionFetchResult.From(
            ranked,
            seen,
            (queries.Count * 12) + (profiles.Count * 8),
            checks,
            discovered.ToList());

        void Collect(JsonElement items, string query, bool searchEstablishesRelevance)
        {
            var before = discussions.Count;
            foreach (var item in items.EnumerateArray())
            {
                var id = ReadString(item, "id");
                var url = ReadString(item, "webVideoUrl") ?? ReadString(item, "url");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || !seen.Add(id))
                    continue;

                var author = ReadAuthor(item);
                var handle = ReadHandle(item);
                var text = ReadString(item, "text") ?? url;
                var title = TrimTitle(text);
                var prohibited = KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, text)
                    || KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, text, author)
                    || KinshasaSocialPostFilter.LooksLikeCongoBrazzaville($"{title} {text}");
                if (!prohibited && handle is not null)
                    discovered.Add(handle);
                if (!(searchEstablishesRelevance || KinshasaSocialPostFilter.IsKinshasaRelevant(null, title, text))
                    || prohibited)
                    continue;

                var score = BuzzGate.Score(
                    ReadInt(item, "diggCount"),
                    ReadInt(item, "commentCount"),
                    ReadInt(item, "shareCount"));
                if (!BuzzGate.IsBuzzing(score, minScore))
                    continue;

                discussions.Add(new SourceFeedDiscussion
                {
                    ExternalId = id,
                    ExternalUrl = url,
                    Title = title,
                    Body = text,
                    OriginalAuthor = author,
                    CommunitySlug = CongoleseSocialTopic.Slug(title, text),
                    PublishedAt = ReadPublished(item),
                    LikeCount = item.TryGetProperty("diggCount", out _) ? ReadInt(item, "diggCount") : null,
                    ViewCount = ReadInt(item, "playCount"),
                    SourceReactions = SourceReactionReader.Read(item),
                    EngagementScore = score,
                    Status = "active",
                    VideoUrls = [url],
                });
            }

            var kept = discussions.Count - before;
            if (query.StartsWith('@'))
                checks.Add(new DiscoveryCheck(query.TrimStart('@'), kept));
            Console.WriteLine($"  TikTok \"{query}\": kept {kept}.");
        }
    }

    private static string TrimTitle(string text)
    {
        var line = text.Replace('\n', ' ').Trim();
        return line.Length <= 140 ? line : line[..140].Trim();
    }

    private static List<SourceFeedDiscussion> RankReserve(List<SourceFeedDiscussion> items)
    {
        var ranked = items
            .OrderByDescending(item => item.EngagementScore ?? 0)
            .ToList();
        if (ranked.Count <= 1)
            return ranked;

        var lead = ranked[0].CommunitySlug ?? "";
        return ranked
            .OrderByDescending(item => string.Equals(item.CommunitySlug, lead, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.EngagementScore ?? 0)
            .ToList();
    }

    private static string? ReadHandle(JsonElement item)
    {
        if (!item.TryGetProperty("authorMeta", out var meta))
            return null;
        var name = ReadString(meta, "name");
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().TrimStart('@');
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
