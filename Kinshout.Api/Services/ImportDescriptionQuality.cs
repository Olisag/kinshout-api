namespace Kinshout.Api.Services;

public static class ImportDescriptionQuality
{
    private static readonly string[] PlaceholderPhrases =
    [
        "voir l'annonce",
        "voir annonce",
        "voir le lien",
        "contactez",
        "plus d'infos",
        "plus de détails",
        "plus de details",
        "détails sur le site",
        "details sur le site",
        "annonce importée",
        "annonce importee",
    ];

    public static bool IsMeaningful(string? description, string? title, string? summary)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;

        var trimmed = description.Trim();
        if (trimmed.Length < 48)
            return false;

        if (!string.IsNullOrWhiteSpace(title)
            && string.Equals(trimmed, title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(summary)
            && string.Equals(trimmed, summary.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lower = trimmed.ToLowerInvariant();
        if (PlaceholderPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return false;

        return true;
    }
}
