using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

public sealed class ApifyFacebookPostsScraperProvider(HttpClient http, ExternalProviderSettings settings) : IExternalDiscussionProvider
{
    private const string DefaultActorId = "scraper_one/facebook-posts-search";

    public string Name => settings.Name;

    public async Task<DiscussionFetchResult> FetchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApifyActorId))
            settings.ApifyActorId = DefaultActorId;

        var client = new ApifyClient(http, settings);
        var queries = ResolveQueries();
        var discussions = new List<SourceFeedDiscussion>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxItems = settings.ResultsLimit > 0 ? settings.ResultsLimit : 20;
        var minEngagement = ResolveMinEngagement();
        var minPostedAt = ResolveMinPostedAt();
        var rawCount = 0;
        var filteredOut = 0;

        foreach (var query in queries)
        {
            if (discussions.Count >= maxItems)
                break;

            using var doc = await client.RunActorAndGetDatasetAsync(BuildInput(query), ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine($"  Apify Facebook posts: unexpected dataset for query '{query}'.");
                continue;
            }

            rawCount += doc.RootElement.GetArrayLength();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (discussions.Count >= maxItems)
                    break;

                var rawId = ReadString(item, "postId") ?? ReadString(item, "id");
                if (!string.IsNullOrWhiteSpace(rawId))
                    seenIds.Add(rawId);

                try
                {
                    var mapped = MapPost(item, minEngagement, minPostedAt);
                    if (mapped is null)
                    {
                        filteredOut++;
                        continue;
                    }

                    if (!keptIds.Add(mapped.ExternalId ?? mapped.ExternalUrl ?? Guid.NewGuid().ToString("N")))
                        continue;

                    discussions.Add(mapped);
                }
                catch (Exception ex)
                {
                    filteredOut++;
                    Console.WriteLine($"  Apify Facebook post parse skipped: {ex.Message}");
                }
            }
        }

        if (rawCount > 0)
        {
            Console.WriteLine(
                $"  Apify Facebook posts: kept {discussions.Count}/{rawCount} visual Kinshasa topics ({filteredOut} filtered out).");
        }

        return DiscussionFetchResult.From(discussions, seenIds);
    }

    private List<string> ResolveQueries()
    {
        var queries = settings.SearchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queries.Count == 0)
            queries.Add("Kinshasa");
        return queries;
    }

    private object BuildInput(string query)
    {
        var count = Math.Max(5, Math.Min(15, settings.ResultsLimit > 0 ? settings.ResultsLimit : 12));
        var end = DateTime.UtcNow.Date;
        var start = ResolveMinPostedAt().Date;

        return new
        {
            query,
            location = settings.DefaultCity,
            searchType = string.IsNullOrWhiteSpace(settings.PopularUrl) ? "top" : settings.PopularUrl.Trim().ToLowerInvariant(),
            resultsCount = count,
            startDate = start.ToString("yyyy-MM-dd"),
            endDate = end.ToString("yyyy-MM-dd"),
        };
    }

    private SourceFeedDiscussion? MapPost(JsonElement item, int minEngagement, DateTime minPostedAt)
    {
        var id = ReadString(item, "postId") ?? ReadString(item, "id");
        var url = ReadString(item, "url") ?? ReadString(item, "postUrl");
        var text = ReadString(item, "postText") ?? ReadString(item, "text") ?? ReadString(item, "message");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text))
            return null;

        var (images, videos) = ExtractMedia(item);
        // Visual-first feed: skip text-only Facebook posts.
        if (images.Count == 0 && videos.Count == 0)
            return null;

        var author = item.TryGetProperty("author", out var authorNode)
            ? ReadAuthorName(authorNode)
            : null;
        author ??= ReadString(item, "authorName") ?? ReadString(item, "pageName");
        var location = ReadString(item, "location");
        var title = BuildTitle(text);
        var body = text.Trim();

        if (!KinshasaSocialPostFilter.IsKinshasaRelevant(location, title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikePoliticsOrConflict(title, body))
            return null;

        var likes = ReadInt(item, "reactionsCount") ?? ReadInt(item, "likesCount") ?? 0;
        var comments = ReadInt(item, "commentsCount") ?? 0;
        var shares = ReadInt(item, "sharesCount") ?? 0;
        var score = likes + comments * 2 + shares * 3;
        if (score < minEngagement)
            return null;

        var postedAt = ReadTimestamp(item, "timestamp") ?? ReadTimestamp(item, "date");
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

    private static (List<string> Images, List<string> Videos) ExtractMedia(JsonElement item)
    {
        var images = new List<string>();
        var videos = new List<string>();

        if (item.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                var type = (ReadString(attachment, "type") ?? string.Empty).ToLowerInvariant();
                var mediaUrl = ReadString(attachment, "url")
                    ?? ReadString(attachment, "imageUrl")
                    ?? ReadString(attachment, "photoUrl")
                    ?? ReadString(attachment, "videoUrl");
                if (string.IsNullOrWhiteSpace(mediaUrl))
                    continue;

                if (type.Contains("video", StringComparison.Ordinal))
                    videos.Add(mediaUrl);
                else
                    images.Add(mediaUrl);
            }
        }

        var singleImage = ReadString(item, "imageUrl") ?? ReadString(item, "photoUrl");
        if (!string.IsNullOrWhiteSpace(singleImage))
            images.Add(singleImage);

        var singleVideo = ReadString(item, "videoUrl");
        if (!string.IsNullOrWhiteSpace(singleVideo))
            videos.Add(singleVideo);

        return (
            images.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            videos.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList());
    }

    private static string BuildTitle(string text)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        var sentence = normalized.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var title = string.IsNullOrWhiteSpace(sentence) ? normalized : sentence;
        return title.Length <= 120 ? title : title[..117] + "...";
    }

    private static string? ReadAuthorName(JsonElement author)
    {
        if (author.ValueKind == JsonValueKind.String)
            return author.GetString();

        if (author.ValueKind == JsonValueKind.Object)
            return ReadString(author, "name") ?? ReadString(author, "username");

        return null;
    }

    private static DateTime? ReadTimestamp(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            if (epoch > 1_000_000_000_000)
                epoch /= 1000;
            return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }

        if (value.ValueKind == JsonValueKind.String)
            return HtmlScrapeHelpers.ParseLooseDate(value.GetString());

        return null;
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
