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

        if (!KinshasaSocialPostFilter.IsKinshasaRelevant(null, title, body))
            return null;

        if (KinshasaSocialPostFilter.LooksLikeSpamOrAd(title, body))
            return null;

        var likes = ReadInt(item, "likeCount") ?? ReadInt(item, "likes") ?? 0;
        var replies = ReadInt(item, "replyCount") ?? ReadInt(item, "replies") ?? 0;
        var retweets = ReadInt(item, "retweetCount") ?? ReadInt(item, "retweets") ?? 0;
        var score = likes + replies * 2 + retweets * 3;
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
        };
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
