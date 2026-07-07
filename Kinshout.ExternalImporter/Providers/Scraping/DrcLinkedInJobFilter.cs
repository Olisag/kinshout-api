namespace Kinshout.ExternalImporter.Providers.Scraping;

internal static class DrcLinkedInJobFilter
{
    public static bool IsDrcKinshasaJob(string? location, string? title, string? description)
    {
        if (IsRepublicOfCongoNotDrc(location))
            return false;

        var blob = string.Join(" ", new[] { location, title, description }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (blob.Contains("Kinshasa", StringComparison.OrdinalIgnoreCase))
            return true;

        if (blob.Contains("Democratic Republic of the Congo", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("République démocratique du Congo", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("République démocratique", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("RDC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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

        if (location.Contains("Republic of the Congo", StringComparison.OrdinalIgnoreCase)
            || location.Contains("République du Congo", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Brazzaville", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Pointe-Noire", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
