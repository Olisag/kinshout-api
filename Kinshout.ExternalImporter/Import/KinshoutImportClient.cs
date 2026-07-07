using System.Net.Http.Json;
using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

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
                p => p.LastRunAtUtc.ToUniversalTime(),
                StringComparer.OrdinalIgnoreCase);
    }

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
}
