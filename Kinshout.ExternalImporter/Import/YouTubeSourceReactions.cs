using System.Text.Json;

namespace Kinshout.ExternalImporter.Import;

/// <summary>Optional official API ingestion; no comment author identities are retained.</summary>
public static class YouTubeSourceReactions
{
    public static async Task<IReadOnlyList<string>?> FetchWhenEnabledAsync(HttpClient http, string key, string videoId, CancellationToken ct)
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("KINSHOUT_IMPORTER_FETCH_SOURCE_REACTIONS"), out var enabled) || !enabled)
            return null;
        var url = "https://www.googleapis.com/youtube/v3/commentThreads?part=snippet&order=relevance&maxResults=20&textFormat=plainText"
            + "&videoId=" + Uri.EscapeDataString(videoId) + "&key=" + Uri.EscapeDataString(key);
        try
        {
            using var response = await http.GetAsync(url, ct);
            // Disabled comments, quota exhaustion or missing permission must not abort a feed refresh.
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("items", out var items)) return [];
            var texts = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("snippet", out var thread) || !thread.TryGetProperty("topLevelComment", out var comment)
                    || !comment.TryGetProperty("snippet", out var snippet) || !snippet.TryGetProperty("textDisplay", out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text[..Math.Min(text.Length, 1000)]);
            }
            return texts.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }
}
