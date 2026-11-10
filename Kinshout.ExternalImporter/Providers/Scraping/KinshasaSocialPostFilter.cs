namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Kinshasa / DRC social import gate: city life stays; Brazzaville and
/// party politics / armed conflict stay out.
/// Keep term lists aligned with Kinshout.Api PoliticsFeedFilter.
/// </summary>
internal static class KinshasaSocialPostFilter
{
    public static bool IsKinshasaRelevant(string? location, string? title, string? body)
    {
        var blob = string.Join(
            " ",
            new[] { location, title, body }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(blob))
            return false;

        // Congo-Brazzaville is a different country — never treat it as Kinshasa.
        if (LooksLikeCongoBrazzaville(blob))
            return false;

        if (LooksLikeKinshasaOrDrc(blob))
            return true;

        // Congolese music / fashion culture is Kinshasa-relevant even when the post omits the city name.
        if (LooksLikeCongoleseMusicOrFashion(blob))
            return true;

        return false;
    }

    public static bool LooksLikeCongoleseMusicOrFashion(string blob)
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
            "mode congolaise");
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
    /// Politics and armed conflict stay out of the feed. City life, sport,
    /// culture, and education stay.
    /// </summary>
    public static bool LooksLikePoliticsOrConflict(string? title, string? body, string? author = null)
    {
        var blob = $"{title} {body} {author}".ToLowerInvariant();
        return ContainsAny(
            blob,
            // Parties / figures
            "fayulu",
            "kabund",
            "mukebayi",
            "kamerhe",
            "katumbi",
            "tshisekedi",
            "fatshi",
            "kabila",
            "bemba",
            "mlc",
            "fcc ",
            "udps",
            "topcongo",
            "top congo",
            "naanga",
            "nangaa",
            "corneille nangaa",
            "hypolite kanambe",
            "hyppolite kanambe",
            "hippolyte kanambe",
            "kanambe",
            // Armed groups / conflict — including FRN
            " frn",
            "frn ",
            "#frn",
            "(frn",
            "frn/",
            "frn)",
            "front pour la refondation",
            "refondation de la nation",
            "m23",
            "afc/m23",
            "afc-m23",
            "afc m23",
            "fardc",
            "fdlr",
            "codeco",
            " adf",
            "adf ",
            "#adf",
            "rebelle",
            "rebelles",
            "rébellion",
            "rebellion",
            "insurgé",
            "insurge",
            "insurgés",
            "insurges",
            "milice",
            "milices",
            "groupe armé",
            "groupe arme",
            "groupes armés",
            "groupes armes",
            "guerre",
            "guerres",
            "guerre civile",
            "conflit armé",
            "conflit arme",
            "combats ",
            "bataille",
            "attaque armée",
            "attaque armee",
            "rwanda",
            "rwandais",
            "rwandaise",
            "kigali",
            // Institutional politics
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

    /// <summary>Republic of the Congo (Brazzaville), not Congo-Kinshasa / DRC.</summary>
    public static bool LooksLikeCongoBrazzaville(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var blob = text.ToLowerInvariant();

        // Explicit Kinshasa / DRC wins over ambiguous wording.
        if (LooksLikeExplicitKinshasaOrDrc(blob))
            return false;

        return ContainsAny(
            blob,
            "brazzaville",
            "pointe-noire",
            "pointe noire",
            "congo-brazzaville",
            "congo brazzaville",
            "congo-brazza",
            "congo brazza",
            "république du congo",
            "republique du congo",
            "republic of the congo",
            "republic of congo",
            "#congobrazzaville",
            "#brazzaville");
    }

    /// <summary>
    /// Kinshasa city or explicit DRC — not bare "Congo" (that also means Brazzaville).
    /// </summary>
    public static bool LooksLikeKinshasaOrDrc(string blob)
    {
        if (LooksLikeExplicitKinshasaOrDrc(blob))
            return true;

        // "Congolais / congolaise" without Brazzaville markers → DRC culture.
        var lower = blob.ToLowerInvariant();
        if (LooksLikeCongoBrazzaville(blob))
            return false;

        return lower.Contains("congolais", StringComparison.Ordinal)
            || lower.Contains("congolaise", StringComparison.Ordinal);
    }

    private static bool LooksLikeExplicitKinshasaOrDrc(string blob)
    {
        var lower = blob.ToLowerInvariant();
        return ContainsAny(
            lower,
            "kinshasa",
            "congo-kinshasa",
            "congo kinshasa",
            "democratic republic of the congo",
            "république démocratique du congo",
            "republique democratique du congo",
            "république démocratique",
            "republique democratique",
            " rd congo",
            "rd congo",
            "#rdc",
            " rdc",
            "rdc ",
            "rdc:",
            "rdc,",
            "rdc.",
            "(rdc",
            "/rdc",
            " drc",
            "drc ",
            "drc:",
            "#drc");
    }
}
