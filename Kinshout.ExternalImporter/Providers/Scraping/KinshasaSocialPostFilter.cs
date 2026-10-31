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
            || blob.Contains("#RDC", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("Congo", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("Congolais", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("Congolaise", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Congolese culture topics stay relevant even when the post omits the city name.
        if (LooksLikeCongoleseCultureTopic(blob))
            return true;

        return false;
    }

    public static bool LooksLikeCongoleseMusicOrFashion(string blob) =>
        LooksLikeCongoleseCultureTopic(blob);

    public static bool LooksLikeCongoleseCultureTopic(string blob)
    {
        var lower = blob.ToLowerInvariant();
        return ContainsAny(
            lower,
            "fally",
            "ipupa",
            "ferre gola",
            "ferrê gola",
            "wenge",
            "jb mpiana",
            "werrason",
            "werra ",
            "koffi olomide",
            "koffi olomidé",
            "innoss",
            "innos'b",
            "rumba congolaise",
            "ndombolo",
            "sapologie",
            "sapeur",
            "la sape",
            "american in paris",
            "américain à paris",
            "americain a paris",
            "mode kinshasa",
            "mode congolaise",
            // Pastors / churches / gospel
            "pasteur",
            "évêque",
            "eveque",
            "église",
            "eglise",
            "réveil",
            "reveil",
            "culte",
            "gospel",
            "chorale",
            "témoignage",
            "temoignage",
            // Comedians / humour
            "humoriste",
            "comédien",
            "comedien",
            "sketch",
            "stand up",
            "stand-up",
            // Influencers
            "influenceur",
            "influenceuse",
            "tiktok kin",
            "tiktoker",
            "créateur de contenu",
            "createur de contenu",
            // Insolite / viral visuals
            "insolite",
            "viral",
            "phénomène",
            "phenomene",
            "scandale");
    }

    private static bool ContainsAny(string blob, params string[] needles) =>
        needles.Any(n => blob.Contains(n, StringComparison.Ordinal));

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

    /// <summary>
    /// Soft politics / conflict filter for the curated Kinshasa social feed.
    /// Keeps day-to-day city life, sport, culture, education.
    /// </summary>
    public static bool LooksLikePoliticsOrConflict(string? title, string? body)
    {
        var blob = $"{title} {body}".ToLowerInvariant();
        return ContainsAny(
            blob,
            "fayulu",
            "kabund",
            "mukebayi",
            "kamerhe",
            "katumbi",
            "tshisekedi",
            "fcc ",
            "udps",
            "m23",
            "rwanda",
            "rwandais",
            "rwandaise",
            "kigali",
            "naanga",
            "nangaa",
            "corneille nangaa",
            "hypolite kanambe",
            "hyppolite kanambe",
            "hippolyte kanambe",
            "kanambe",
            "afc/m23",
            "rebelle",
            "rebelles",
            "gouvernement",
            "président",
            "president",
            "élection",
            "election",
            "politique",
            "parlement",
            "assemblée nationale",
            "assemblee nationale",
            "coup d'état",
            "coup d'etat");
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
