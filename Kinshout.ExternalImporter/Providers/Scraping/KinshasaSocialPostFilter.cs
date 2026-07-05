namespace Kinshout.ExternalImporter.Providers.Scraping;

internal static class KinshasaSocialPostFilter
{
    public static bool IsKinshasaRelevant(string? location, string? title, string? body)
    {
        if (IsRepublicOfCongoNotDrc(location))
            return false;

        var blob = string.Join(" ", new[] { location, title, body }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (blob.Contains("Kinshasa", StringComparison.OrdinalIgnoreCase))
            return true;

        if (blob.Contains("Democratic Republic of the Congo", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("République démocratique du Congo", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("République démocratique", StringComparison.OrdinalIgnoreCase)
            || blob.Contains(" RDC", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("RDC:", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("#RDC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool LooksLikeSpamOrAd(string? title, string? body)
    {
        var blob = $"{title} {body}".ToLowerInvariant();
        return blob.Contains("promo code")
            || blob.Contains("click here to buy")
            || blob.Contains("dm for price")
            || blob.Contains("whatsapp +")
            || blob.Contains("gagnez de l'argent")
            || blob.Contains("crypto investment");
    }

    private static bool IsRepublicOfCongoNotDrc(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;

        if (location.Contains("Democratic Republic", StringComparison.OrdinalIgnoreCase)
            || location.Contains("République démocratique", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return location.Contains("Republic of the Congo", StringComparison.OrdinalIgnoreCase)
            || location.Contains("République du Congo", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Brazzaville", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Pointe-Noire", StringComparison.OrdinalIgnoreCase);
    }
}
