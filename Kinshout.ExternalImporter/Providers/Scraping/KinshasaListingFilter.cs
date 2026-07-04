using System.Text.Json;

namespace Kinshout.ExternalImporter.Providers.Scraping;

internal static class KinshasaListingFilter
{
    private static readonly HashSet<string> UsStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY",
        "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND",
        "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC",
    };

    private static readonly string[] KinshasaCommunes =
    [
        "Kinshasa", "Gombe", "Ngaliema", "Limete", "Bandalungwa", "Bandal", "Kalamu", "Macampagne", "Masina",
        "Mont Ngafula", "Ndjili", "N'djili", "Kimbanseke", "Maluku", "Selembao", "Kintambo", "Barumbu", "Lingwala",
        "Ngaba", "Makala", "Ngaliema", "Kasa-Vubu", "Bumbu", "Matete", "Lemba", "Ngaliema",
    ];

    public static bool IsKinshasa(JsonElement item, string? title, string? description, string? locationText)
    {
        var city = ReadNestedString(item, "location", "reverse_geocode", "city");
        var state = ReadNestedString(item, "location", "reverse_geocode", "state");
        var displayName = locationText ?? ReadNestedString(item, "location", "reverse_geocode", "city_page", "display_name");

        var subtitle = ReadSubtitles(item);
        var blob = string.Join(" ", new[] { displayName, city, state, subtitle, title, description }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (IsLikelyUnitedStates(displayName, city, state, subtitle))
            return false;

        if (blob.Contains("Kinshasa", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ContainsKinshasaCommune(blob))
            return true;

        if (blob.Contains("RDC", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("République démocratique", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("Democratic Republic of the Congo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelyUnitedStates(string? displayName, string? city, string? state, string? subtitle)
    {
        var blob = string.Join(" ", new[] { displayName, city, state, subtitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (blob.Contains("United States", StringComparison.OrdinalIgnoreCase)
            || blob.Contains(", USA", StringComparison.OrdinalIgnoreCase)
            || blob.Contains(" U.S.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(state) && state.Length == 2 && UsStateCodes.Contains(state))
            return true;

        if (displayName?.Contains(", CA", StringComparison.OrdinalIgnoreCase) == true
            || displayName?.Contains(", NY", StringComparison.OrdinalIgnoreCase) == true
            || displayName?.Contains(", TX", StringComparison.OrdinalIgnoreCase) == true
            || displayName?.Contains("California", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return false;
    }

    private static bool ContainsKinshasaCommune(string blob) =>
        KinshasaCommunes.Any(commune => blob.Contains(commune, StringComparison.OrdinalIgnoreCase));

    public static string? ExtractCommune(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return KinshasaCommunes.FirstOrDefault(commune =>
            commune != "Kinshasa"
            && text.Contains(commune, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadSubtitles(JsonElement item)
    {
        if (!item.TryGetProperty("custom_sub_titles_with_rendering_flags", out var subtitles)
            || subtitles.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Join(" ", subtitles.EnumerateArray()
            .Select(node => node.TryGetProperty("subtitle", out var subtitle) && subtitle.ValueKind == JsonValueKind.String
                ? subtitle.GetString()
                : null)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
