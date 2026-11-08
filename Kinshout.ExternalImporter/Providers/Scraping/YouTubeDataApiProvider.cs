using System.Globalization;
using System.Text;
using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Official YouTube Data API. Living registry channels are polled for new uploads, including Shorts.
/// The buzz pass searches recent videos about each person, including shows that are not on their channel.
/// Classiques channels are fetched only for the one-time backfill.
/// </summary>
public sealed class YouTubeDataApiProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    public string Name => settings.Name;

    public async Task<DiscussionFetchResult> FetchAsync(CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("  YouTube Data API: YOUTUBE_API_KEY is not set, so no videos were fetched.");
            return DiscussionFetchResult.From([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (settings.YouTubeBuzz)
            return await FetchBuzzAsync(apiKey, ct);

        var sources = SourceRegistry.Load()
            .Where(source => source.Import && !string.IsNullOrWhiteSpace(source.YoutubeChannelId))
            .Where(source => settings.ClassiquesBackfill
                ? source.Backfill
                : source.WebSub)
            .ToList();
        var limit = settings.ClassiquesBackfill
            ? 30
            : settings.ResultsLimit > 0 ? settings.ResultsLimit : 15;
        var discussions = new List<SourceFeedDiscussion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var playlistId = UploadsPlaylistId(source.YoutubeChannelId!);
            var ids = await ListUploadIdsAsync(apiKey, playlistId, limit, ct);
            if (ids.Count == 0)
                continue;

            foreach (var id in ids)
                seen.Add(id);

            var videos = await LoadVideosAsync(apiKey, ids, ct);
            foreach (var video in videos)
            {
                if (IsBlocked(video.Title, video.Body, video.OriginalAuthor))
                    continue;
                if (!settings.ClassiquesBackfill && video.ViewCount < BuzzMinimumViews)
                    continue;

                video.OriginalAuthor = source.Name;
                video.CommunitySlug = source.CommunitySlug;
                discussions.Add(video);
            }
        }

        Console.WriteLine($"  YouTube Data API: {discussions.Count} video(s) from {sources.Count} channel(s).");
        return DiscussionFetchResult.From(discussions, seen);
    }

    private async Task<DiscussionFetchResult> FetchBuzzAsync(string apiKey, CancellationToken ct)
    {
        var sources = SourceRegistry.Load().Where(source => source.Import).ToList();
        var publishedAfter = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var discussions = new List<SourceFeedDiscussion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var ids = await SearchBuzzIdsAsync(apiKey, source, publishedAfter, ct);
            if (ids.Count == 0)
                continue;

            var videos = await LoadVideosAsync(apiKey, ids, ct);
            foreach (var video in videos)
            {
                if (string.IsNullOrWhiteSpace(video.ExternalId) || seen.Contains(video.ExternalId))
                    continue;
                if (video.ViewCount < BuzzMinimumViews)
                    continue;
                if (!Mentions(source, video.Title, video.Body))
                    continue;
                if (IsBlocked(video.Title, video.Body, video.OriginalAuthor))
                    continue;

                seen.Add(video.ExternalId);
                video.OriginalAuthor = source.Name;
                video.CommunitySlug = source.CommunitySlug;
                discussions.Add(video);
            }
        }

        var showHits = await FetchShowHitsAsync(apiKey, sources, seen, ct);
        discussions.AddRange(showHits);
        var communityHits = await FetchCommunityBuzzAsync(apiKey, publishedAfter, seen, ct);
        discussions.AddRange(communityHits);

        Console.WriteLine(
            $"  YouTube buzz: kept {discussions.Count} video(s) from {sources.Count} people, {showHits.Count} show hit(s), and {communityHits.Count} community clip(s). Each search costs 100 quota units.");
        return DiscussionFetchResult.From(discussions, seen);
    }

    /// <summary>
    /// Show and personality channels post a lot. Only a recent upload that Congolese viewers
    /// have already watched is kept, then filed under the person it is about.
    /// </summary>
    private async Task<List<SourceFeedDiscussion>> FetchShowHitsAsync(
        string apiKey,
        IReadOnlyList<SourceRegistryEntry> sources,
        HashSet<string> seen,
        CancellationToken ct)
    {
        var shows = sources
            .Where(source => source.HitsOnly && !string.IsNullOrWhiteSpace(source.YoutubeChannelId))
            .ToList();
        var kept = new List<SourceFeedDiscussion>();
        var since = DateTime.UtcNow.AddDays(-7);

        foreach (var show in shows)
        {
            var ids = await ListUploadIdsAsync(apiKey, UploadsPlaylistId(show.YoutubeChannelId!), 15, ct);
            if (ids.Count == 0)
                continue;

            foreach (var video in await LoadVideosAsync(apiKey, ids, ct))
            {
                if (string.IsNullOrWhiteSpace(video.ExternalId) || seen.Contains(video.ExternalId))
                    continue;
                if (video.ViewCount < ShowHitMinimumViews)
                    continue;
                if (video.PublishedAt is null || video.PublishedAt < since)
                    continue;
                if (IsBlocked(video.Title, video.Body, video.OriginalAuthor))
                    continue;

                var subject = FindSubject(sources, show, video.Title, video.Body);
                seen.Add(video.ExternalId);
                video.OriginalAuthor = subject?.Name ?? show.Name;
                video.CommunitySlug = subject?.CommunitySlug ?? show.CommunitySlug;
                kept.Add(video);
            }
        }

        return kept;
    }

    /// <summary>
    /// A few searches for what Congolese people are watching, whoever posted it.
    /// Known names already have their own search.
    /// </summary>
    private async Task<List<SourceFeedDiscussion>> FetchCommunityBuzzAsync(
        string apiKey,
        string publishedAfter,
        HashSet<string> seen,
        CancellationToken ct)
    {
        var kept = new List<SourceFeedDiscussion>();
        foreach (var query in CommunityBuzzQueries)
        {
            var ids = await SearchIdsAsync(apiKey, query, publishedAfter, ct);
            if (ids.Count == 0)
                continue;

            foreach (var video in await LoadVideosAsync(apiKey, ids, ct))
            {
                if (string.IsNullOrWhiteSpace(video.ExternalId) || seen.Contains(video.ExternalId))
                    continue;
                if (video.ViewCount < BuzzMinimumViews)
                    continue;
                if (IsBlocked(video.Title, video.Body, video.OriginalAuthor))
                    continue;

                seen.Add(video.ExternalId);
                video.CommunitySlug = CongoleseSocialTopic.Slug(video.Title, video.Body);
                kept.Add(video);
            }
        }

        return kept;
    }

    private static readonly string[] CommunityBuzzQueries =
    [
        "Kinshasa (humour OR sketch OR potins OR buzz OR insolite)",
        "Kinshasa (quartier OR embouteillage OR marché OR kinois OR tendance)",
        "(Congolais OR Kinois) (diaspora OR Bruxelles OR Paris OR Montréal OR Johannesburg)",
        "\"RDC\" (buzz OR viral OR tendance OR insolite)",
    ];

    private static SourceRegistryEntry? FindSubject(
        IReadOnlyList<SourceRegistryEntry> sources,
        SourceRegistryEntry show,
        string? title,
        string? body)
    {
        SourceRegistryEntry? best = null;
        var bestLength = 0;
        foreach (var source in sources)
        {
            if (ReferenceEquals(source, show) || !source.Import)
                continue;
            if (!Mentions(source, title, body))
                continue;

            var length = source.Mentions.Count > 0
                ? source.Mentions.Max(term => term.Length)
                : source.Name.Length;
            if (length <= bestLength)
                continue;

            best = source;
            bestLength = length;
        }

        return best;
    }

    private async Task<List<string>> SearchBuzzIdsAsync(
        string apiKey,
        SourceRegistryEntry source,
        string publishedAfter,
        CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(source.SearchQuery) ? $"\"{source.Name}\"" : source.SearchQuery.Trim();
        return await SearchIdsAsync(apiKey, query, publishedAfter, ct);
    }

    private async Task<List<string>> SearchIdsAsync(
        string apiKey,
        string query,
        string publishedAfter,
        CancellationToken ct)
    {
        var url = "https://www.googleapis.com/youtube/v3/search"
            + "?part=snippet&type=video&order=viewCount&maxResults=5&safeSearch=strict&videoEmbeddable=true"
            + $"&publishedAfter={Uri.EscapeDataString(publishedAfter)}"
            + $"&q={Uri.EscapeDataString(query)}"
            + $"&key={Uri.EscapeDataString(apiKey)}";
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  YouTube search failed ({(int)response.StatusCode}).");
            return [];
        }

        using var document = JsonDocument.Parse(body);
        var ids = new List<string>();
        if (!document.RootElement.TryGetProperty("items", out var items))
            return ids;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idNode)
                && idNode.TryGetProperty("videoId", out var videoId)
                && videoId.GetString() is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private const int BuzzMinimumViews = 1000;
    private const int ShowHitMinimumViews = 5000;

    private static bool Mentions(SourceRegistryEntry source, string? title, string? body)
    {
        var haystack = Fold($"{title} {body}");
        var terms = source.Mentions.Count > 0 ? source.Mentions : [source.Name];
        return terms.Any(term =>
        {
            var needle = Fold(term);
            return needle.Length >= 3 && ContainsTerm(haystack, needle);
        });
    }

    private static bool ContainsTerm(string haystack, string needle)
    {
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var afterIndex = index + needle.Length;
            var after = afterIndex >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIndex]);
            if (before && after)
                return true;
            index = afterIndex;
        }

        return false;
    }

    private static bool IsBlocked(string? title, string? body, string? author)
    {
        if (KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, body, author))
            return true;

        var haystack = Fold($"{title} {body} {author}");
        return AdultTerms.Any(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static readonly string[] AdultTerms =
    [
        "porn", "sexe", "sexuel", "onlyfans", "questions osees", "question osee",
    ];

    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string UploadsPlaylistId(string channelId) =>
        channelId.StartsWith("UC", StringComparison.Ordinal)
            ? "UU" + channelId[2..]
            : channelId;

    private async Task<List<string>> ListUploadIdsAsync(
        string apiKey,
        string playlistId,
        int limit,
        CancellationToken ct)
    {
        var url = "https://www.googleapis.com/youtube/v3/playlistItems"
            + $"?part=contentDetails&maxResults={Math.Clamp(limit, 1, 50)}"
            + $"&playlistId={Uri.EscapeDataString(playlistId)}&key={Uri.EscapeDataString(apiKey)}";
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  YouTube playlist {playlistId} failed ({(int)response.StatusCode}).");
            return [];
        }

        using var document = JsonDocument.Parse(body);
        var ids = new List<string>();
        if (!document.RootElement.TryGetProperty("items", out var items))
            return ids;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("contentDetails", out var details)
                && details.TryGetProperty("videoId", out var videoId)
                && videoId.GetString() is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<List<SourceFeedDiscussion>> LoadVideosAsync(
        string apiKey,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        var url = "https://www.googleapis.com/youtube/v3/videos"
            + "?part=snippet,statistics"
            + $"&id={Uri.EscapeDataString(string.Join(',', ids))}"
            + $"&key={Uri.EscapeDataString(apiKey)}";
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  YouTube videos.list failed ({(int)response.StatusCode}).");
            return [];
        }

        using var document = JsonDocument.Parse(body);
        var discussions = new List<SourceFeedDiscussion>();
        if (!document.RootElement.TryGetProperty("items", out var items))
            return discussions;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || !item.TryGetProperty("snippet", out var snippet))
                continue;

            var title = snippet.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
            var description = snippet.TryGetProperty("description", out var descriptionNode)
                ? descriptionNode.GetString()
                : null;
            DateTime? published = null;
            if (snippet.TryGetProperty("publishedAt", out var publishedNode)
                && DateTime.TryParse(publishedNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                published = parsed;
            }

            var watchUrl = $"https://www.youtube.com/watch?v={id}";
            var thumb = snippet.TryGetProperty("thumbnails", out var thumbs)
                && thumbs.TryGetProperty("high", out var high)
                && high.TryGetProperty("url", out var thumbUrl)
                ? thumbUrl.GetString()
                : null;
            var views = ReadStat(item, "viewCount");
            var likes = ReadStat(item, "likeCount");
            discussions.Add(new SourceFeedDiscussion
            {
                ExternalId = id,
                ExternalUrl = watchUrl,
                Title = string.IsNullOrWhiteSpace(title) ? id : title.Trim(),
                Body = string.IsNullOrWhiteSpace(description) ? watchUrl : description.Trim(),
                OriginalAuthor = snippet.TryGetProperty("channelTitle", out var channelNode)
                    ? channelNode.GetString()
                    : null,
                PublishedAt = published,
                ViewCount = views,
                EngagementScore = views + (likes * 5),
                Status = "active",
                ImageUrls = string.IsNullOrWhiteSpace(thumb) ? [] : [thumb],
                VideoUrls = [watchUrl],
            });
        }

        return discussions;
    }

    private static int ReadStat(JsonElement item, string name)
    {
        if (!item.TryGetProperty("statistics", out var stats) || !stats.TryGetProperty(name, out var node))
            return 0;

        return int.TryParse(node.GetString(), out var value) ? value : 0;
    }

    private string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey) && !settings.ApiKey.Contains("${", StringComparison.Ordinal))
            return settings.ApiKey.Trim();

        return Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
    }
}
