using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>Daily TikTok pull for registry handles. Caps stay small (~16).</summary>
public sealed class ApifyTikTokScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "clockworks/tiktok-scraper";

    public string Name => settings.Name;

    public async Task<DiscussionFetchResult> FetchAsync(CancellationToken ct)
    {
        var profiles = SourceRegistry.Load()
            .Where(source => source.Import && !string.IsNullOrWhiteSpace(source.Tiktok))
            .Select(source => source.Tiktok!.Trim().TrimStart('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (profiles.Count == 0)
        {
            Console.WriteLine("  TikTok: no registry handles to import.");
            return DiscussionFetchResult.From([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(settings.ApifyActorId))
            settings.ApifyActorId = DefaultActorId;

        var limit = settings.ResultsLimit > 0 ? settings.ResultsLimit : 16;
        var client = new ApifyClient(http, settings);
        using var doc = await client.RunActorAndGetDatasetAsync(new
        {
            profiles,
            resultsPerPage = limit,
            profileSorting = "latest",
            shouldDownloadVideos = false,
            shouldDownloadCovers = false,
        }, ct);

        var discussions = new List<SourceFeedDiscussion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptByHandle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return DiscussionFetchResult.From(discussions, seen);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var url = ReadString(item, "webVideoUrl") ?? ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
                continue;

            var handle = ReadHandle(item);
            var bucket = string.IsNullOrWhiteSpace(handle) ? id : handle;
            keptByHandle.TryGetValue(bucket, out var kept);
            if (kept >= limit)
                continue;

            seen.Add(id);
            keptByHandle[bucket] = kept + 1;
            var author = ReadAuthor(item);
            var text = ReadString(item, "text") ?? url;
            var source = SourceRegistry.MatchTikTok(handle) ?? SourceRegistry.MatchAuthor(author);
            discussions.Add(new SourceFeedDiscussion
            {
                ExternalId = id,
                ExternalUrl = url,
                Title = TrimTitle(text),
                Body = text,
                OriginalAuthor = author,
                CommunitySlug = source?.CommunitySlug,
                PublishedAt = ReadPublished(item),
                EngagementScore = ReadInt(item, "playCount") + (ReadInt(item, "diggCount") * 5),
                Status = "active",
                VideoUrls = [url],
            });
        }

        Console.WriteLine($"  TikTok: kept {discussions.Count} video(s) from {profiles.Count} profile(s).");
        return DiscussionFetchResult.From(discussions, seen);
    }

    private static string TrimTitle(string text)
    {
        var line = text.Replace('\n', ' ').Trim();
        return line.Length <= 140 ? line : line[..140].Trim();
    }

    private static string? ReadHandle(JsonElement item)
    {
        if (!item.TryGetProperty("authorMeta", out var meta))
            return null;

        return ReadString(meta, "name") ?? ReadString(meta, "uniqueId");
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
