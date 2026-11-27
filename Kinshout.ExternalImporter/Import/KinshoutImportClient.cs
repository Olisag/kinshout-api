using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers;

namespace Kinshout.ExternalImporter.Import;

public sealed class KinshoutImportClient(HttpClient http, KinshoutApiSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ImportExternalAdvertsResponseDto> ImportAsync(
        IReadOnlyList<ImportExternalAdvertDto> adverts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), settings.ImportPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new ImportExternalAdvertsRequestDto(adverts), options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout import failed ({(int)response.StatusCode}): {body}");

        return JsonSerializer.Deserialize<ImportExternalAdvertsResponseDto>(body, JsonOptions)
            ?? new ImportExternalAdvertsResponseDto(0, 0, 0, adverts.Count);
    }

    public async Task<IReadOnlySet<string>> FetchKnownAdvertKeysAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.KnownAdvertsPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout known-adverts failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<ImportKnownAdvertsResponseDto>(body, JsonOptions);
        if (parsed?.Adverts is null || parsed.Adverts.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return parsed.Adverts
            .Where(a => !string.IsNullOrWhiteSpace(a.Provider) && !string.IsNullOrWhiteSpace(a.ExternalId))
            .Select(a => ImportAdvertKeys.Format(a.Provider, a.ExternalId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ImportExternalDiscussionsResponseDto> ImportDiscussionsAsync(
        IReadOnlyList<ImportExternalDiscussionDto> discussions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), settings.ImportDiscussionsPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new ImportExternalDiscussionsRequestDto(discussions), options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout discussion import failed ({(int)response.StatusCode}): {body}");

        return JsonSerializer.Deserialize<ImportExternalDiscussionsResponseDto>(body, JsonOptions)
            ?? new ImportExternalDiscussionsResponseDto(0, 0, 0, discussions.Count);
    }

    public async Task<IReadOnlySet<string>> FetchKnownDiscussionKeysAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.KnownDiscussionsPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout known-discussions failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<ImportKnownDiscussionsResponseDto>(body, JsonOptions);
        if (parsed?.Discussions is null || parsed.Discussions.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return parsed.Discussions
            .Where(d => !string.IsNullOrWhiteSpace(d.Provider) && !string.IsNullOrWhiteSpace(d.ExternalId))
            .Select(d => ImportDiscussionKeys.Format(d.Provider, d.ExternalId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> FetchDiscussionImportStateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.DiscussionImportStatePath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout discussion-import-state failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<ImportDiscussionImportStateResponseDto>(body, JsonOptions);
        if (parsed?.Providers is null || parsed.Providers.Count == 0)
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        return parsed.Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Provider))
            .ToDictionary(
                p => p.Provider.Trim(),
                p => AsUtc(p.LastRunAtUtc),
                StringComparer.OrdinalIgnoreCase);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    public async Task RecordDiscussionImportRunAsync(string provider, DateTime runAtUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.DiscussionImportRunsPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { provider, runAt = runAtUtc }, options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout discussion-import-runs failed ({(int)response.StatusCode}): {body}");
    }

    public async Task<IReadOnlyList<string>> FetchYouTubeMetricTargetsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.YouTubeMetricTargetsPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout youtube targets failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<YouTubeMetricTargetsResponse>(body, JsonOptions);
        return parsed?.ExternalIds ?? [];
    }

    public async Task RecordYouTubeMetricsAsync(IReadOnlyList<YouTubeMetricPoint> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.YouTubeMetricsPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { metrics }, options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout youtube metrics failed ({(int)response.StatusCode}): {body}");
    }

    public async Task DropUnavailableSourcesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            "api/imports/discussions/drop-unavailable?limit=80");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout drop-unavailable failed ({(int)response.StatusCode}): {body}");

        Console.WriteLine($"Unavailable sources: {body}");
    }

    public async Task<string> UploadVideoAsync(Stream stream, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), "api/imports/media");
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Add(file, "file", "clip.mp4");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout video upload failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<UploadedMediaResponse>(body, JsonOptions);
        if (string.IsNullOrWhiteSpace(parsed?.Url))
            throw new InvalidOperationException("Kinshout video upload did not return a url.");

        return parsed.Url;
    }

    public async Task<IReadOnlyList<TikTokFileBacklogItem>> TikToksMissingFileAsync(int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            $"api/imports/discussions/tiktok-without-file?limit={limit}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout tiktok backlog failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<TikTokBacklogResponse>(body, JsonOptions);
        return parsed?.Items ?? [];
    }

    public async Task<IReadOnlyList<TikTokPosterBacklogItem>> TikToksNeedingPosterAsync(int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            $"api/imports/discussions/tiktok-without-poster?limit={limit}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout tiktok posters failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<TikTokPosterBacklogResponse>(body, JsonOptions);
        return parsed?.Items ?? [];
    }

    public async Task AttachTikTokPosterAsync(string videoUrl, Stream image, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            "api/imports/discussions/tiktok-poster");
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "cover.jpg");
        content.Add(new StringContent(videoUrl), "videoUrl");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout tiktok poster failed ({(int)response.StatusCode}): {body}");
    }

    public async Task AttachTikTokFileAsync(string externalId, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            "api/imports/discussions/tiktok-file");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { externalId, url }, options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout tiktok file attach failed ({(int)response.StatusCode}): {body}");
    }

    public async Task<IReadOnlyList<SourceRegistryEntry>> FetchSourceRegistryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            throw new InvalidOperationException("KinshoutApi:ImportKey is required.");

        var endpoint = new Uri(
            new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            settings.SourceRegistryPath.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kinshout sources failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<SourceRegistryFile>(body, JsonOptions);
        return parsed?.Sources ?? [];
    }

    public async Task<DiscoveryBudgetSnapshot?> TryFetchDiscoveryBudgetAsync(string provider, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey))
            return null;

        try
        {
            var endpoint = new Uri(
                new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
                "api/imports/discovery-budget?provider=" + Uri.EscapeDataString(provider));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<DiscoveryBudgetSnapshot>(body, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  Discovery budget unavailable ({ex.Message}).");
            return null;
        }
    }

    public async Task TryRecordDiscoverySpendAsync(
        string provider,
        int resultsRequested,
        IReadOnlyList<DiscoveryCheck> checks,
        IReadOnlyList<string> discovered,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImportKey)
            || (resultsRequested <= 0 && checks.Count == 0 && discovered.Count == 0))
            return;

        try
        {
            var endpoint = new Uri(
                new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
                "api/imports/discovery-spend?provider=" + Uri.EscapeDataString(provider));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(
                    new
                    {
                        resultsRequested,
                        checks = checks.Select(check => new { handle = check.Handle, kept = check.Kept }),
                        discovered,
                    },
                    options: JsonOptions),
            };
            request.Headers.TryAddWithoutValidation("X-Kinshout-Import-Key", settings.ImportKey);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"  Discovery spend was not recorded ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  Discovery spend was not recorded ({ex.Message}).");
        }
    }

    private sealed record YouTubeMetricTargetsResponse(IReadOnlyList<string>? ExternalIds);

    private sealed record UploadedMediaResponse(string? Url);

    private sealed record TikTokBacklogResponse(IReadOnlyList<TikTokFileBacklogItem>? Items);

    private sealed record TikTokPosterBacklogResponse(IReadOnlyList<TikTokPosterBacklogItem>? Items);

    private sealed class SourceRegistryFile
    {
        public List<SourceRegistryEntry> Sources { get; set; } = [];
    }
}

public sealed class DiscoveryBudgetSnapshot
{
    public int RemainingImports { get; set; }
    public int RemainingResults { get; set; }
    public List<DiscoverySourceSnapshot> Sources { get; set; } = [];
    public List<string> NextProfiles { get; set; } = [];
}

public sealed class DiscoverySourceSnapshot
{
    public string Handle { get; set; } = "";
    public int Checks { get; set; }
    public int Kept { get; set; }
    public int Imports { get; set; }
}
