using System.Net.Http.Headers;
using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers.Scraping;

namespace Kinshout.ExternalImporter.Import;

/// <summary>
/// Downloads only the TikTok clips we are about to publish, plus a small backlog
/// of posts already on the feed that still have no file.
/// </summary>
internal sealed class TikTokVideoArchiver(
    HttpClient http,
    ExternalProviderSettings providerSettings,
    KinshoutImportClient importClient)
{
    internal const int CombinedCap = 12;
    internal const int BacklogCap = 8;
    private const long MaxBytes = 28 * 1024 * 1024;
    private const int DownloadTimeoutSeconds = 360;

    public async Task<List<ImportExternalDiscussionDto>> AttachAsync(
        List<ImportExternalDiscussionDto> fresh,
        CancellationToken ct)
    {
        var backlog = await TryLoadBacklogAsync(ct);
        var chosen = Choose(fresh, backlog);
        if (chosen.Count == 0)
            return fresh;

        Console.WriteLine($"TikTok files: asking for {chosen.Count} clip(s).");
        var apify = new ApifyClient(http, providerSettings);
        using var dataset = await apify.RunActorAndGetDatasetAsync(
            new
            {
                postURLs = chosen,
                resultsPerPage = chosen.Count,
                shouldDownloadVideos = true,
                shouldDownloadCovers = false,
                shouldDownloadSlideshowImages = false,
                shouldDownloadSubtitles = false,
            },
            ct,
            DownloadTimeoutSeconds);

        var files = TikTokDownloadedFiles.Read(dataset);
        var hostedByVideoId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var videoId = TikTokDownloadedFiles.VideoId(file.WatchUrl);
            if (videoId is null || hostedByVideoId.ContainsKey(videoId))
                continue;

            var hosted = await TryUploadAsync(file.FileUrl, ct);
            if (hosted is not null)
                hostedByVideoId[videoId] = hosted;
        }

        var updated = fresh
            .Select(item => SwapInHostedFile(item, hostedByVideoId))
            .ToList();

        foreach (var item in backlog)
        {
            var videoId = TikTokDownloadedFiles.VideoId(item.ExternalUrl);
            if (videoId is null || !hostedByVideoId.TryGetValue(videoId, out var hosted))
                continue;

            try
            {
                await importClient.AttachTikTokFileAsync(item.ExternalId, hosted, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TikTok file for {item.ExternalId} was stored but not attached: {ex.Message}");
            }
        }

        Console.WriteLine($"TikTok files: stored {hostedByVideoId.Count} of {chosen.Count}.");
        return updated;
    }

    /// <summary>A TikTok is published only when we stored the mp4.</summary>
    internal static List<ImportExternalDiscussionDto> WithStoredFile(
        List<ImportExternalDiscussionDto> items) =>
        items.Where(HasStoredFile).ToList();

    internal static bool HasStoredFile(ImportExternalDiscussionDto item) =>
        item.VideoUrls?.Any(url =>
            url.Contains("/uploads/videos/", StringComparison.OrdinalIgnoreCase)) == true;

    internal static List<string> Choose(
        IReadOnlyList<ImportExternalDiscussionDto> fresh,
        IReadOnlyList<TikTokFileBacklogItem> backlog)
    {
        var freshIds = new HashSet<string>(StringComparer.Ordinal);
        var urls = new List<string>();
        foreach (var item in fresh)
        {
            var url = WatchUrl(item);
            var videoId = TikTokDownloadedFiles.VideoId(url);
            if (url is null || videoId is null || !freshIds.Add(videoId))
                continue;

            urls.Add(url);
            if (urls.Count >= CombinedCap)
                return urls;
        }

        var freshCount = urls.Count;
        foreach (var item in backlog)
        {
            var videoId = TikTokDownloadedFiles.VideoId(item.ExternalUrl);
            if (videoId is null || freshIds.Contains(videoId))
                continue;
            if (urls.Count - freshCount >= BacklogCap)
                break;

            urls.Add(item.ExternalUrl);
            freshIds.Add(videoId);
            if (urls.Count >= CombinedCap)
                break;
        }

        return urls;
    }

    private async Task<IReadOnlyList<TikTokFileBacklogItem>> TryLoadBacklogAsync(CancellationToken ct)
    {
        try
        {
            return await importClient.TikToksMissingFileAsync(BacklogCap, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TikTok backlog skipped: {ex.Message}");
            return [];
        }
    }

    private async Task<string?> TryUploadAsync(string fileUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            if (fileUrl.Contains("api.apify.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResolveToken());
            }
            else if (fileUrl.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Referer", "https://www.tiktok.com/");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || type.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            if (response.Content.Headers.ContentLength > MaxBytes)
                return null;

            await using var remote = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            while (true)
            {
                var read = await remote.ReadAsync(chunk, ct);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaxBytes)
                    return null;

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length < 1024)
                return null;

            buffer.Position = 0;
            return await importClient.UploadVideoAsync(buffer, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TikTok file download failed: {ex.Message}");
            return null;
        }
    }

    private static ImportExternalDiscussionDto SwapInHostedFile(
        ImportExternalDiscussionDto item,
        IReadOnlyDictionary<string, string> hostedByVideoId)
    {
        var videoId = TikTokDownloadedFiles.VideoId(WatchUrl(item));
        if (videoId is null || !hostedByVideoId.TryGetValue(videoId, out var hosted))
            return item;

        var images = item.ImageUrls ?? [];
        return item with { VideoUrls = [hosted], ImageUrls = images };
    }

    private static string? WatchUrl(ImportExternalDiscussionDto item)
    {
        var external = item.Source.ExternalUrl;
        if (TikTokDownloadedFiles.VideoId(external) is not null)
            return external;

        return item.VideoUrls?.FirstOrDefault(url => TikTokDownloadedFiles.VideoId(url) is not null);
    }

    private string ResolveToken()
    {
        var token = First(
            providerSettings.ApifyToken,
            providerSettings.ApiKey,
            providerSettings.AccessToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Apify token required to download a TikTok file.");

        return token;
    }

    private static string? First(params string?[] values) =>
        values.Select(value => value?.Trim().Trim('\r', '\n')).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

internal static class TikTokDownloadedFiles
{
    internal readonly record struct DownloadedFile(string WatchUrl, string FileUrl);

    internal static IReadOnlyList<DownloadedFile> Read(JsonDocument dataset)
    {
        if (dataset.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<DownloadedFile>();
        foreach (var item in dataset.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("isSlideshow", out var slideshow)
                && slideshow.ValueKind == JsonValueKind.True)
                continue;

            var watch = ReadString(item, "webVideoUrl");
            var file = FirstPlayableFile(item);
            if (string.IsNullOrWhiteSpace(watch) || file is null)
                continue;

            found.Add(new DownloadedFile(watch, file));
        }

        return found;
    }

    internal static string? VideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        const string marker = "/video/";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var rest = url[(index + marker.Length)..];
        var end = rest.IndexOfAny(['?', '/', '&']);
        var id = (end < 0 ? rest : rest[..end]).Trim();
        return id.Length == 0 ? null : id;
    }

    private static string? FirstPlayableFile(JsonElement item)
    {
        if (item.TryGetProperty("mediaUrls", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            string? fallback = null;
            foreach (var entry in media.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;

                var url = entry.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("api.apify.com", StringComparison.OrdinalIgnoreCase))
                    return url;

                fallback ??= url;
            }

            if (fallback is not null)
                return fallback;
        }

        if (item.TryGetProperty("videoMeta", out var meta))
        {
            var download = ReadString(meta, "downloadAddr") ?? ReadString(meta, "originalDownloadAddr");
            if (download is not null)
                return download;
        }

        return ReadString(item, "downloadAddr");
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
