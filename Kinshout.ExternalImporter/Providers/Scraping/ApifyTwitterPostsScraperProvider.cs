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
                var mapped = MapTweet(item, minEngagement, minPostedAt, searchEstablishesRelevance: settings.DiscoveryRotation);
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
        var maxTweets = FeedOperatingProfile.ResultCap(
            settings.Provider,
            settings.ResultsLimit > 0 ? settings.ResultsLimit : 50,
            settings.OperatingProfile);
        var sort = string.IsNullOrWhiteSpace(settings.RecentUrl) ? "Top" : settings.RecentUrl.Trim();

        return new
        {
            query,
            sort,
            maxTweets,
        };
    }

    private string BuildQuery()
    {
        var query = settings.SearchQueries.FirstOrDefault(q => !string.IsNullOrWhiteSpace(q))
            ?? $"Kinshasa lang:fr min_faves:{Math.Max(5, ResolveMinEngagement() / 4)}";
        if (settings.DiscoveryRotation)
        {
            query = FeedDiscoveryPlanner.BuildXQuery(
                query,
                DateTime.UtcNow,
                FeedCandidateCatalogue.Subjects("x", SourceRegistry.Load()));
            Console.WriteLine("  X discovery query ready.");
        }

        if (settings.SincePublishedAt is { } since
            && !query.Contains("since:", StringComparison.OrdinalIgnoreCase))
        {
            query = $"{query} since:{since.ToUniversalTime():yyyy-MM-dd}";
        }

        return query;
    }

    private SourceFeedDiscussion? MapTweet(JsonElement item, int minEngagement, DateTime minPostedAt, bool searchEstablishesRelevance = false)
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

        if (!searchEstablishesRelevance && !KinshasaSocialPostFilter.IsKinshasaRelevant(null, title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, body, author))
            return null;

        var likes = ReadInt(item, "likeCount") ?? ReadInt(item, "likes") ?? 0;
        var replies = ReadInt(item, "replyCount") ?? ReadInt(item, "replies") ?? 0;
        var retweets = ReadInt(item, "retweetCount") ?? ReadInt(item, "retweets") ?? 0;
        var score = BuzzGate.Score(likes, replies, retweets);
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
            CommunitySlug = CongoleseSocialTopic.Slug(title, body),
            PublishedAt = postedAt,
            LikeCount = ReadInt(item, "likeCount") ?? ReadInt(item, "likes"),
            ViewCount = ReadInt(item, "viewCount") ?? ReadInt(item, "views") ?? 0,
            SourceReactions = SourceReactionReader.Read(item),
            EngagementScore = score,
            Status = "active",
            ImageUrls = images,
            VideoUrls = videos,
        };
    }

    private static (List<string> Images, List<string> Videos) ExtractMedia(JsonElement item, string text)
    {
        var images = new List<string>();
        var videos = new List<string>();

        void AddImage(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || LooksLikeVideoUrl(url))
                return;
            images.Add(url);
        }

        void AddVideo(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || LooksLikeStillImageUrl(url))
                return;
            videos.Add(url);
        }

        if (item.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Object)
            CollectEntityMedia(entities, AddImage, AddVideo);

        if (item.TryGetProperty("extendedEntities", out var extended) && extended.ValueKind == JsonValueKind.Object)
            CollectEntityMedia(extended, AddImage, AddVideo);
        else if (item.TryGetProperty("extended_entities", out var extendedSnake) && extendedSnake.ValueKind == JsonValueKind.Object)
            CollectEntityMedia(extendedSnake, AddImage, AddVideo);

        // Some Apify shapes expose parallel mediaUrls / mediaTypes arrays.
        CollectParallelMediaArrays(item, AddImage, AddVideo);

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text,
                     "https?://[^\\s<>\"'`\\)\\]]+",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var link = match.Value.TrimEnd(')', ']', '.', ',', ';');
            if (!link.Contains("twimg.com/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (LooksLikeVideoUrl(link))
                AddVideo(link);
            else
                AddImage(link);
        }

        var distinctImages = images.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var distinctVideos = videos.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Keep X video posters (pbs.twimg.com …_thumb) alongside clips so the API
        // can set PreviewUrl to a still instead of the MP4. ToMediaDtos consumes
        // them as previews and hides them from the photo gallery.

        // Never keep the same URL in both lists.
        var videoSet = new HashSet<string>(distinctVideos, StringComparer.OrdinalIgnoreCase);
        distinctImages = distinctImages.Where(url => !videoSet.Contains(url)).ToList();

        return (
            distinctImages.Take(10).ToList(),
            distinctVideos.Take(5).ToList());
    }

    private static void CollectEntityMedia(
        JsonElement container,
        Action<string?> addImage,
        Action<string?> addVideo)
    {
        if (!container.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            return;

        foreach (var m in media.EnumerateArray())
        {
            var type = (ReadString(m, "type") ?? ReadString(m, "mediaType") ?? string.Empty)
                .ToLowerInvariant();
            var poster = ReadString(m, "media_url_https")
                ?? ReadString(m, "media_url")
                ?? ReadString(m, "thumbnailUrl")
                ?? ReadString(m, "thumbnail_url");
            var videoUrl = ReadBestVideoUrl(m);

            if (type.Contains("video", StringComparison.Ordinal)
                || type.Contains("animated_gif", StringComparison.Ordinal)
                || type.Contains("gif", StringComparison.Ordinal))
            {
                // media_url_https on X videos is the poster JPEG — keep it as an
                // image for PreviewUrl, never as a playable video URL.
                if (!string.IsNullOrWhiteSpace(videoUrl))
                {
                    addVideo(videoUrl);
                    if (LooksLikeStillImageUrl(poster))
                        addImage(poster);
                }
                else
                {
                    addImage(poster);
                }

                continue;
            }

            addImage(poster ?? ReadString(m, "mediaUrl") ?? ReadString(m, "url"));
        }
    }

    private static void CollectParallelMediaArrays(
        JsonElement item,
        Action<string?> addImage,
        Action<string?> addVideo)
    {
        if (!item.TryGetProperty("mediaUrls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            return;

        string[]? types = null;
        if (item.TryGetProperty("mediaTypes", out var typesNode) && typesNode.ValueKind == JsonValueKind.Array)
        {
            types = typesNode.EnumerateArray()
                .Select(t => t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "")
                .ToArray();
        }

        var index = 0;
        foreach (var urlNode in urls.EnumerateArray())
        {
            var url = urlNode.ValueKind == JsonValueKind.String ? urlNode.GetString() : null;
            var type = types is not null && index < types.Length
                ? types[index].ToLowerInvariant()
                : string.Empty;
            index++;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (type.Contains("video", StringComparison.Ordinal)
                || type.Contains("gif", StringComparison.Ordinal)
                || LooksLikeVideoUrl(url))
            {
                if (LooksLikeStillImageUrl(url))
                    addImage(url);
                else
                    addVideo(url);
            }
            else
            {
                addImage(url);
            }
        }
    }

    private static string? ReadBestVideoUrl(JsonElement media)
    {
        foreach (var key in new[] { "video_url", "videoUrl", "mediaUrl", "media_url" })
        {
            var candidate = ReadString(media, key);
            if (LooksLikeVideoUrl(candidate))
                return candidate;
        }

        if (TryPickBestVariant(media, "video_info", out var fromInfo))
            return fromInfo;
        if (TryPickBestVariant(media, "videoInfo", out var fromInfoCamel))
            return fromInfoCamel;
        if (media.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array
            && TryPickBestVariantFromArray(variants, out var fromVariants))
        {
            return fromVariants;
        }

        return null;
    }

    private static bool TryPickBestVariant(JsonElement media, string infoKey, out string? url)
    {
        url = null;
        if (!media.TryGetProperty(infoKey, out var info) || info.ValueKind != JsonValueKind.Object)
            return false;
        if (!info.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
            return false;
        return TryPickBestVariantFromArray(variants, out url);
    }

    /// <summary>
    /// Prefer ~480–720p / mid bitrate for Kinshasa networks instead of the
    /// largest amplify variant (often 1280p+). Target band: 600k–2.2M bps.
    /// </summary>
    private static bool TryPickBestVariantFromArray(JsonElement variants, out string? url)
    {
        url = null;
        string? preferred = null;
        var preferredScore = int.MinValue;
        string? fallback = null;
        var fallbackBitrate = int.MaxValue;
        string? any = null;
        var anyBitrate = -1;

        const int targetMinBitrate = 600_000;
        const int targetMaxBitrate = 2_200_000;
        const int idealBitrate = 1_200_000;

        foreach (var variant in variants.EnumerateArray())
        {
            var contentType = (ReadString(variant, "content_type")
                ?? ReadString(variant, "contentType")
                ?? string.Empty).ToLowerInvariant();
            var candidate = ReadString(variant, "url");
            if (string.IsNullOrWhiteSpace(candidate) || !LooksLikeVideoUrl(candidate))
                continue;
            if (contentType.Contains("mpegURL", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bitrate = ReadInt(variant, "bitrate") ?? 0;
            var widthHint = EstimateWidthFromUrl(candidate);

            // Prefer mid-band bitrate; among those, prefer ~640–720 width.
            if (bitrate >= targetMinBitrate && bitrate <= targetMaxBitrate)
            {
                var score = -Math.Abs(bitrate - idealBitrate);
                if (widthHint is >= 480 and <= 720)
                    score += 500_000;
                else if (widthHint is > 720 and <= 960)
                    score += 100_000;

                if (score > preferredScore)
                {
                    preferredScore = score;
                    preferred = candidate;
                }
            }

            // Fallback: smallest usable mp4 above ~200k (avoid tiny preview-only).
            if (bitrate > 0 && bitrate < fallbackBitrate && bitrate >= 200_000)
            {
                fallbackBitrate = bitrate;
                fallback = candidate;
            }

            if (bitrate >= anyBitrate)
            {
                anyBitrate = bitrate;
                any = candidate;
            }
        }

        url = preferred ?? fallback ?? any;
        return !string.IsNullOrWhiteSpace(url);
    }

    /// <summary>Parse <c>/vid/720x1280/</c> style path segments when present.</summary>
    private static int? EstimateWidthFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            uri.AbsolutePath,
            @"/(\d{3,4})x(\d{3,4})/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups[1].Value, out var a)
            || !int.TryParse(match.Groups[2].Value, out var b))
        {
            return null;
        }

        // Path is usually width x height for landscape, height x width for vertical.
        return Math.Min(a, b);
    }

    /// <summary>X stores poster JPEGs on pbs.twimg.com; real clips live on video.twimg.com.</summary>
    private static bool LooksLikeStillImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var path = uri.AbsolutePath;
        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"\.(avif|gif|jpe?g|png|webp)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (host.Equals("video.twimg.com", StringComparison.OrdinalIgnoreCase))
            return false;

        if (host.Equals("pbs.twimg.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".twimg.com", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/media/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("_thumb/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("video_thumb", StringComparison.OrdinalIgnoreCase)
                || path.Contains("ext_tw_video_thumb", StringComparison.OrdinalIgnoreCase)
                || path.Contains("amplify_video_thumb", StringComparison.OrdinalIgnoreCase)
                || path.Contains("tweet_video_thumb", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var format = ExtractQueryValue(uri.Query, "format");
            if (!string.IsNullOrWhiteSpace(format)
                && System.Text.RegularExpressions.Regex.IsMatch(format, @"^(avif|gif|jpe?g|png|webp)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair[1]))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static bool LooksLikeVideoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (LooksLikeStillImageUrl(url))
            return false;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (host.Equals("video.twimg.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return System.Text.RegularExpressions.Regex.IsMatch(
            uri.AbsolutePath,
            @"\.(mp4|mov|webm|m4v|m3u8)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
