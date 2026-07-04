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
}
