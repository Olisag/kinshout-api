using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers;

public sealed class JsonFeedAdvertProvider(HttpClient http, ExternalProviderSettings settings) : IExternalAdvertProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Name => settings.Name;

    public async Task<IReadOnlyList<SourceFeedAdvert>> FetchAsync(CancellationToken ct)
    {
        var adverts = new List<SourceFeedAdvert>();
        await FetchEndpointAsync(settings.RecentUrl, adverts, ct);
        await FetchEndpointAsync(settings.PopularUrl, adverts, ct);

        return adverts
            .Where(a => !string.IsNullOrWhiteSpace(a.ExternalUrl) && !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.ExternalId ?? a.ExternalUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private async Task FetchEndpointAsync(string? url, List<SourceFeedAdvert> adverts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        using var response = await http.SendAsync(ProviderHttp.CreateRequest(url, settings), ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        adverts.AddRange(ParseFeed(document.RootElement));
    }

    private static IEnumerable<SourceFeedAdvert> ParseFeed(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return DeserializeArray(root);

        if (root.TryGetProperty("adverts", out var adverts) && adverts.ValueKind == JsonValueKind.Array)
            return DeserializeArray(adverts);

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return DeserializeArray(items);

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            return DeserializeArray(results);

        return [];
    }

    private static IEnumerable<SourceFeedAdvert> DeserializeArray(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            var advert = item.Deserialize<SourceFeedAdvert>(JsonOptions);
            if (advert is not null)
                yield return advert;
        }
    }
}
