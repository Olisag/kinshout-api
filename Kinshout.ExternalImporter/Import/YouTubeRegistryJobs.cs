using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Import;

public sealed class YouTubeRegistryJobs(HttpClient http, ImporterSettings settings)
{
    private const string Hub = "https://pubsubhubbub.appspot.com/subscribe";

    public async Task RenewWebSubAsync(CancellationToken ct)
    {
        var callback = Environment.GetEnvironmentVariable("YOUTUBE_WEBSUB_CALLBACK")
            ?? "https://kinshout-api-prod.azurewebsites.net/api/youtube/websub";
        var secret = Environment.GetEnvironmentVariable("YOUTUBE_WEBSUB_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.WriteLine("WebSub renewal skipped: YOUTUBE_WEBSUB_SECRET is not set.");
            return;
        }
        var sources = SourceRegistry.Load()
            .Where(source => source.Import && source.WebSub && !string.IsNullOrWhiteSpace(source.YoutubeChannelId))
            .ToList();

        foreach (var source in sources)
        {
            var topic = "https://www.youtube.com/xml/feeds/videos.xml?channel_id=" + source.YoutubeChannelId;
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["hub.mode"] = "subscribe",
                ["hub.callback"] = callback,
                ["hub.topic"] = topic,
                ["hub.verify"] = "async",
                ["hub.lease_seconds"] = "604800",
                ["hub.secret"] = secret,
            });
            using var response = await http.PostAsync(Hub, content, ct);
            Console.WriteLine(
                $"WebSub {source.Name}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    public async Task RefreshStatsAsync(CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("YouTube stats skipped: YOUTUBE_API_KEY is not set.");
            return;
        }

        var client = new KinshoutImportClient(http, settings.KinshoutApi);
        var ids = await client.FetchYouTubeMetricTargetsAsync(ct);
        if (ids.Count == 0)
        {
            Console.WriteLine("YouTube stats: no clips from the last 72 hours.");
            return;
        }

        var url = "https://www.googleapis.com/youtube/v3/videos"
            + "?part=statistics,snippet"
            + $"&id={Uri.EscapeDataString(string.Join(',', ids))}"
            + $"&key={Uri.EscapeDataString(apiKey)}";
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"YouTube videos.list failed ({(int)response.StatusCode}).");

        using var document = JsonDocument.Parse(body);
        var metrics = new List<YouTubeMetricPoint>();
        if (document.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                DateTime? published = null;
                if (item.TryGetProperty("snippet", out var snippet)
                    && snippet.TryGetProperty("publishedAt", out var publishedNode)
                    && DateTime.TryParse(publishedNode.GetString(), out var parsed))
                {
                    published = parsed.ToUniversalTime();
                }

                metrics.Add(new YouTubeMetricPoint(
                    id,
                    ReadStat(item, "viewCount"),
                    ReadStat(item, "likeCount"),
                    ReadStat(item, "commentCount"),
                    published));
            }
        }

        await client.RecordYouTubeMetricsAsync(metrics, ct);
        Console.WriteLine($"YouTube stats stored for {metrics.Count} clip(s).");
    }

    private static int ReadStat(JsonElement item, string name)
    {
        if (!item.TryGetProperty("statistics", out var stats) || !stats.TryGetProperty(name, out var node))
            return 0;

        return int.TryParse(node.GetString(), out var value) ? value : 0;
    }
}

public sealed record YouTubeMetricPoint(
    string ExternalId,
    int Views,
    int Likes,
    int Comments,
    DateTime? PublishedAt);
