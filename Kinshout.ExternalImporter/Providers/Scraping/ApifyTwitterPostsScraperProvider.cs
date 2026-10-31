using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed class ApifyTwitterPostsScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "seemuapps/x-tweet-scraper";

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
            Console.WriteLine("  Apify X posts: unexpected dataset format.");
            return DiscussionFetchResult.From(discussions, seenIds);
        }

        var rawCount = doc.RootElement.GetArrayLength();
        var filteredOut = 0;
        var minEngagement = ResolveMinEngagement();
        var minPostedAt = ResolveMinPostedAt();
        var maxItems = settings.ResultsLimit > 0 ? settings.ResultsLimit : 50;
        var kept = 0;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (kept >= maxItems)
                break;

            var rawId = ReadString(item, "tweetId") ?? ReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(rawId))
                seenIds.Add(rawId);

            try
            {
                var mapped = MapTweet(item, minEngagement, minPostedAt);
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
                Console.WriteLine($"  Apify X post parse skipped: {ex.Message}");
            }
        }

        if (rawCount > 0)
        {
            Console.WriteLine(
                $"  Apify X posts: kept {discussions.Count}/{rawCount} Kinshasa topics ({filteredOut} filtered out).");
        }

        var deduped = discussions
            .GroupBy(d => d.ExternalId ?? d.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return DiscussionFetchResult.From(deduped, seenIds);
    }

    private object BuildInput()
    {
        var query = BuildQuery();
        var maxTweets = settings.ResultsLimit > 0 ? settings.ResultsLimit : 50;
        var queryType = string.IsNullOrWhiteSpace(settings.RecentUrl) ? "Top" : settings.RecentUrl.Trim();
        if (queryType.Equals("Latest", StringComparison.OrdinalIgnoreCase))
            queryType = "Latest";
        else
            queryType = "Top";

        return new
        {
            query,
            queryType,
            // Backward-compatible alias some actor builds still read.
            sort = queryType,
            maxItems = maxTweets,
            maxTweets,
        };
    }

    private string BuildQuery()
    {
        // Prefer media-bearing queries first when several are configured.
        var query = settings.SearchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .OrderByDescending(q => q.Contains("has:media", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? $"Kinshasa has:media lang:fr min_faves:{Math.Max(5, ResolveMinEngagement() / 4)}";

        if (settings.SincePublishedAt is { } since
            && !query.Contains("since:", StringComparison.OrdinalIgnoreCase))
        {
            query = $"{query} since:{since.ToUniversalTime():yyyy-MM-dd}";
        }

        return query;
    }

    private SourceFeedDiscussion? MapTweet(JsonElement item, int minEngagement, DateTime minPostedAt)
    {
        if (item.TryGetProperty("isRetweet", out var rt) && rt.ValueKind == JsonValueKind.True)
            return null;

        var id = ReadString(item, "tweetId") ?? ReadString(item, "id");
        var url = ReadString(item, "url") ?? ReadString(item, "twitterUrl");
        var text = ReadString(item, "text") ?? ReadString(item, "fullText");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text))
            return null;

        var author = item.TryGetProperty("author", out var authorNode)
            ? ReadAuthorName(authorNode)
            : null;
        author ??= ReadString(item, "username");
        var title = BuildTitle(text);
        var body = text.Trim();
        var (images, videos) = ExtractMedia(item, body);

        var likes = ReadInt(item, "likeCount") ?? ReadInt(item, "likes") ?? 0;
        var replies = ReadInt(item, "replyCount") ?? ReadInt(item, "replies") ?? 0;
        var retweets = ReadInt(item, "retweetCount") ?? ReadInt(item, "retweets") ?? 0;
        var score = likes + replies * 2 + retweets * 3;

        var hasVisual = images.Count > 0 || videos.Count > 0 || ContainsVisualLink(body);
        // Visual-first by default; popular text-only trends still qualify.
        var isPopularTrend = score >= Math.Max(minEngagement * 4, 120);
        if (!hasVisual && !isPopularTrend)
            return null;

        if (!KinshasaSocialPostFilter.IsKinshasaRelevant(null, title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, body))
            return null;

        if (score < minEngagement)
            return null;

        var postedAt = ReadDate(item, "createdAt") ?? ReadDate(item, "date");
        if (postedAt is { } published && published.ToUniversalTime() < minPostedAt)
            return null;

        return new SourceFeedDiscussion
        {
            ExternalId = id,
            ExternalUrl = url,
            Title = title,
            Body = body,
            OriginalAuthor = author,
            PublishedAt = postedAt,
            EngagementScore = score,
            Status = "active",
            ImageUrls = images,
            VideoUrls = videos,
        };
    }

    private static bool ContainsVisualLink(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("youtube.com/", StringComparison.Ordinal)
            || lower.Contains("youtu.be/", StringComparison.Ordinal)
            || lower.Contains("tiktok.com/", StringComparison.Ordinal)
            || lower.Contains("instagram.com/", StringComparison.Ordinal)
            || lower.Contains("fb.watch/", StringComparison.Ordinal)
            || lower.Contains("twimg.com/", StringComparison.Ordinal);
    }

    private static (List<string> Images, List<string> Videos) ExtractMedia(JsonElement item, string text)
    {
        var images = new List<string>();
        var videos = new List<string>();

        CollectMediaFromNode(item, images, videos);

        foreach (var propertyName in new[]
                 {
                     "entities",
                     "extended_entities",
                     "extendedEntities",
                     "legacy",
                     "media",
                     "photos",
                     "videos",
                     "mediaEntities",
                     "attachments",
                 })
        {
            if (!item.TryGetProperty(propertyName, out var node))
                continue;

            CollectMediaFromNode(node, images, videos);
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text,
                     "https?://[^\\s<>\"'`\\)\\]]+",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var link = match.Value.TrimEnd(')', ']', '.', ',', ';');
            if (IsTwitterImageUrl(link))
                images.Add(link);
            else if (IsTwitterVideoUrl(link))
                videos.Add(link);
        }

        return (
            images.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            videos.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList());
    }

    private static void CollectMediaFromNode(
        JsonElement node,
        List<string> images,
        List<string> videos)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                CollectMediaItem(child, images, videos);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in media.EnumerateArray())
                CollectMediaItem(child, images, videos);
        }

        if (node.TryGetProperty("photos", out var photos) && photos.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in photos.EnumerateArray())
                CollectMediaItem(child, images, videos, forceImage: true);
        }

        if (node.TryGetProperty("videos", out var videoNodes) && videoNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in videoNodes.EnumerateArray())
                CollectMediaItem(child, images, videos, forceVideo: true);
        }

        if (node.TryGetProperty("mediaUrls", out var mediaUrls) && mediaUrls.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in mediaUrls.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String)
                {
                    var url = child.GetString();
                    if (IsTwitterVideoUrl(url))
                        videos.Add(url!);
                    else if (!string.IsNullOrWhiteSpace(url) && IsTwitterImageUrl(url))
                        images.Add(url!);
                }
                else
                {
                    CollectMediaItem(child, images, videos);
                }
            }
        }

        // Only treat the node itself as a media item when it clearly carries media fields.
        if (LooksLikeMediaObject(node))
            CollectMediaItem(node, images, videos);
    }

    private static bool LooksLikeMediaObject(JsonElement media)
    {
        if (media.ValueKind != JsonValueKind.Object)
            return false;

        return media.TryGetProperty("media_url_https", out _)
            || media.TryGetProperty("mediaUrl", out _)
            || media.TryGetProperty("video_info", out _)
            || media.TryGetProperty("videoInfo", out _)
            || media.TryGetProperty("videoVariants", out _)
            || media.TryGetProperty("thumbnailUrl", out _)
            || media.TryGetProperty("thumbnail_url", out _)
            || (media.TryGetProperty("type", out var typeNode)
                && typeNode.ValueKind == JsonValueKind.String
                && (typeNode.GetString() is "photo" or "video" or "animated_gif"));
    }

    private static void CollectMediaItem(
        JsonElement media,
        List<string> images,
        List<string> videos,
        bool forceImage = false,
        bool forceVideo = false)
    {
        if (media.ValueKind == JsonValueKind.String)
        {
            var url = media.GetString();
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (forceVideo || IsTwitterVideoUrl(url))
                videos.Add(url);
            else
                images.Add(url);
            return;
        }

        if (media.ValueKind != JsonValueKind.Object)
            return;

        var type = (
            ReadString(media, "type")
            ?? ReadString(media, "mediaType")
            ?? ReadString(media, "media_type")
            ?? string.Empty
        ).ToLowerInvariant();

        var isVideo = forceVideo
            || type.Contains("video", StringComparison.Ordinal)
            || type.Contains("animated_gif", StringComparison.Ordinal)
            || type.Contains("gif", StringComparison.Ordinal);

        var isImage = forceImage
            || type.Contains("photo", StringComparison.Ordinal)
            || type.Contains("image", StringComparison.Ordinal);

        // Prefer playable mp4 variants when present.
        var playableVideo = PickBestVideoVariant(media);
        if (!string.IsNullOrWhiteSpace(playableVideo))
        {
            videos.Add(playableVideo);
            var poster = ReadString(media, "media_url_https")
                ?? ReadString(media, "mediaUrl")
                ?? ReadString(media, "previewUrl")
                ?? ReadString(media, "thumbnailUrl")
                ?? ReadString(media, "thumbnail_url");
            if (!string.IsNullOrWhiteSpace(poster) && IsTwitterImageUrl(poster))
                images.Add(poster);
            return;
        }

        var mediaUrl = ReadString(media, "media_url_https")
            ?? ReadString(media, "mediaUrl")
            ?? ReadString(media, "url")
            ?? ReadString(media, "previewUrl")
            ?? ReadString(media, "thumbnailUrl")
            ?? ReadString(media, "thumbnail_url")
            ?? ReadString(media, "src");

        if (string.IsNullOrWhiteSpace(mediaUrl))
            return;

        if (isVideo && !IsTwitterImageUrl(mediaUrl))
        {
            videos.Add(mediaUrl);
            return;
        }

        if (isVideo && IsTwitterImageUrl(mediaUrl))
        {
            // Thumbnail only — keep as image so the feed still shows media.
            images.Add(mediaUrl);
            return;
        }

        if (isImage || IsTwitterImageUrl(mediaUrl) || !isVideo)
            images.Add(mediaUrl);
    }

    private static string? PickBestVideoVariant(JsonElement media)
    {
        JsonElement variants = default;
        if (media.TryGetProperty("video_info", out var videoInfo)
            && videoInfo.ValueKind == JsonValueKind.Object
            && videoInfo.TryGetProperty("variants", out variants))
        {
            // ok
        }
        else if (media.TryGetProperty("videoInfo", out var videoInfoCamel)
                 && videoInfoCamel.ValueKind == JsonValueKind.Object
                 && videoInfoCamel.TryGetProperty("variants", out variants))
        {
            // ok
        }
        else if (media.TryGetProperty("videoVariants", out var directVariants))
        {
            variants = directVariants;
        }
        else if (media.TryGetProperty("variants", out var topVariants))
        {
            variants = topVariants;
        }
        else
        {
            return null;
        }

        if (variants.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        var bestBitrate = -1;
        foreach (var variant in variants.EnumerateArray())
        {
            if (variant.ValueKind != JsonValueKind.Object)
                continue;

            var contentType = (
                ReadString(variant, "content_type")
                ?? ReadString(variant, "contentType")
                ?? string.Empty
            ).ToLowerInvariant();
            var url = ReadString(variant, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (!contentType.Contains("mp4", StringComparison.Ordinal)
                && !url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bitrate = 0;
            if (variant.TryGetProperty("bitrate", out var bitrateNode)
                && bitrateNode.ValueKind == JsonValueKind.Number)
            {
                bitrate = bitrateNode.TryGetInt32(out var value) ? value : 0;
            }

            if (bitrate >= bestBitrate)
            {
                bestBitrate = bitrate;
                best = url;
            }
        }

        return best;
    }

    private static bool IsTwitterImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var lower = url.ToLowerInvariant();
        return lower.Contains("pbs.twimg.com/", StringComparison.Ordinal)
            || (lower.Contains("twimg.com/", StringComparison.Ordinal)
                && (lower.Contains("/media/", StringComparison.Ordinal)
                    || lower.Contains("format=jpg", StringComparison.Ordinal)
                    || lower.Contains("format=png", StringComparison.Ordinal)
                    || lower.Contains(".jpg", StringComparison.Ordinal)
                    || lower.Contains(".jpeg", StringComparison.Ordinal)
                    || lower.Contains(".png", StringComparison.Ordinal)
                    || lower.Contains(".webp", StringComparison.Ordinal)));
    }

    private static bool IsTwitterVideoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var lower = url.ToLowerInvariant();
        return lower.Contains("video.twimg.com/", StringComparison.Ordinal)
            || (lower.Contains("twimg.com/", StringComparison.Ordinal)
                && lower.Contains(".mp4", StringComparison.Ordinal));
    }

    private static string BuildTitle(string text)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        var withoutTags = System.Text.RegularExpressions.Regex.Replace(normalized, @"#\w+", "").Trim();
        var title = withoutTags.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? withoutTags;
        return title.Length <= 120 ? title : title[..117] + "...";
    }

    private static string? ReadAuthorName(JsonElement author)
    {
        if (author.ValueKind == JsonValueKind.Undefined || author.ValueKind == JsonValueKind.Null)
            return null;

        if (author.ValueKind == JsonValueKind.String)
            return author.GetString();

        return ReadString(author, "userName")
            ?? ReadString(author, "username")
            ?? ReadString(author, "name");
    }

    private static DateTime? ReadDate(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return HtmlScrapeHelpers.ParseLooseDate(value.GetString());
    }

    private int ResolveMaxAgeDays() => settings.MaxAdvertAgeDays > 0 ? settings.MaxAdvertAgeDays : 7;

    private DateTime ResolveMinPostedAt() =>
        settings.SincePublishedAt?.ToUniversalTime()
        ?? DateTime.UtcNow.AddDays(-ResolveMaxAgeDays());
    private int ResolveMinEngagement() => settings.MinEngagementScore > 0 ? settings.MinEngagementScore : 20;

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
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
