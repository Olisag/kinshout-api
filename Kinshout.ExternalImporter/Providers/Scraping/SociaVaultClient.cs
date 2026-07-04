using System.Text.Json;
using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

internal sealed class SociaVaultClient(HttpClient http, ExternalProviderSettings settings)
{
    private const string BaseUrl = "https://api.sociavault.com";

    public async Task<(double Lat, double Lng)> ResolveLocationAsync(CancellationToken ct)
    {
        if (settings.Latitude is { } lat && settings.Longitude is { } lng)
            return (lat, lng);

        var query = string.IsNullOrWhiteSpace(settings.DefaultCity) ? "Kinshasa" : settings.DefaultCity;
        var url = $"{BaseUrl}/v1/scrape/facebook-marketplace/location-search?query={Uri.EscapeDataString(query)}";
        using var doc = await GetJsonAsync(url, ct);
        EnsureSuccess(doc);

        var locations = GetListingsContainer(doc.RootElement, "data", "locations");
        var first = locations.FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SociaVault location search returned no results for '{query}'.");

        var resolvedLat = ReadDouble(first, "latitude");
        var resolvedLng = ReadDouble(first, "longitude");
        if (resolvedLat is null || resolvedLng is null)
            throw new InvalidOperationException($"SociaVault location search missing coordinates for '{query}'.");

        return (resolvedLat.Value, resolvedLng.Value);
    }

    public async Task<JsonDocument> SearchAsync(
        string query,
        double lat,
        double lng,
        string? cursor,
        CancellationToken ct)
    {
        var parts = new List<string>
        {
            $"query={Uri.EscapeDataString(query)}",
            $"lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"lng={lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"radius_km={Math.Max(1, settings.SearchRadiusKm)}",
            "sort_by=creation_time_descend",
            "date_listed=30",
            "availability=available",
            $"count={Math.Min(50, Math.Max(10, settings.MaxPages * 10))}",
        };

        if (!string.IsNullOrWhiteSpace(cursor))
            parts.Add($"cursor={Uri.EscapeDataString(cursor)}");

        var url = $"{BaseUrl}/v1/scrape/facebook-marketplace/search?{string.Join('&', parts)}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<JsonDocument> GetItemAsync(string id, CancellationToken ct)
    {
        var url = $"{BaseUrl}/v1/scrape/facebook-marketplace/item?id={Uri.EscapeDataString(id)}";
        return await GetJsonAsync(url, ct);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-API-Key", ResolveApiKey());

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SociaVault request failed ({(int)response.StatusCode}): {Trim(body, 400)}");

        return JsonDocument.Parse(body);
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            return settings.ApiKey;

        if (!string.IsNullOrWhiteSpace(settings.AccessToken))
            return settings.AccessToken;

        if (settings.Headers.TryGetValue("X-API-Key", out var header) && !string.IsNullOrWhiteSpace(header))
            return header;

        throw new InvalidOperationException(
            "SociaVault API key required. Set providers[].apiKey, accessToken, or headers.X-API-Key to ${SOCIAVAULT_API_KEY}.");
    }

    internal static void EnsureSuccess(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            var error = doc.RootElement.TryGetProperty("error", out var errorNode)
                ? errorNode.GetString()
                : "Unknown SociaVault error";
            throw new InvalidOperationException(error ?? "SociaVault request failed.");
        }
    }

    internal static IEnumerable<JsonElement> GetListingsContainer(JsonElement root, params string[] path)
    {
        var node = root;
        foreach (var segment in path)
        {
            if (!node.TryGetProperty(segment, out node))
                yield break;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                yield return item;
            yield break;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    yield return property.Value;
            }
        }
    }

    internal static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    internal static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    internal static DateTime? ReadDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return HtmlScrapeHelpers.ParseLooseDate(value.GetString());
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
