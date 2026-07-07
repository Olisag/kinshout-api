namespace Kinshout.Api.Services;

public sealed class SearchQueryHints
{
    public IReadOnlyList<string> LocationTerms { get; init; } = [];
    public string? SubcategorySlug { get; init; }
}

public static class SearchQueryResolver
{
    private static readonly string[] KinshasaCommunes =
    [
        "gombe", "ngaliema", "limete", "bandalungwa", "bandal", "kalamu", "macampagne", "masina",
        "mont ngafula", "ndjili", "kimbanseke", "maluku", "selembao", "kintambo", "barumbu", "lingwala",
        "ngaba", "makala", "kasa vubu", "bumbu", "matete", "lemba", "binza",
    ];

    private static readonly (string Token, string Label)[] CityTerms =
    [
        ("kinshasa", "Kinshasa"),
        ("lubumbashi", "Lubumbashi"),
    ];

    /// <summary>
    /// Extracts structured hints from a search query (location, subcategory).
    /// Does not route to category/topic browse — that requires explicit categoryId/topicId on the request.
    /// </summary>
    public static SearchQueryHints ParseHints(string query)
    {
        var parsed = SearchQueryParser.Parse(query);
        return ParseHints(parsed);
    }

    internal static SearchQueryHints ParseHints(ParsedSearchQuery parsed)
    {
        if (string.IsNullOrWhiteSpace(parsed.OriginalQuery))
            return new SearchQueryHints();

        var normalized = SearchTextNormalizer.Normalize(parsed.SubjectText);
        if (normalized.Length < 2)
            normalized = SearchTextNormalizer.Normalize(parsed.OriginalQuery);
        if (normalized.Length < 2)
            return new SearchQueryHints();

        var locationTerms = ExtractLocationTerms(normalized);
        var textWithoutLocations = RemoveLocations(normalized, locationTerms);
        var subcategoryText = string.IsNullOrWhiteSpace(textWithoutLocations) ? normalized : textWithoutLocations;
        var parentSlug = InferParentSlug(subcategoryText) ?? InferParentSlug(normalized);

        string? subcategory = null;
        if (parentSlug is not null)
        {
            var inferred = InferSubcategorySlug(subcategoryText, parentSlug, parsed.IntentHint);
            if (inferred is not null
                && (locationTerms.Count > 0 || NamesExplicitSubcategory(subcategoryText, parentSlug)))
            {
                subcategory = inferred;
            }
        }

        return new SearchQueryHints
        {
            LocationTerms = locationTerms,
            SubcategorySlug = subcategory,
        };
    }

    internal static string? InferSubcategorySlug(string normalized, string parentSlug, string? intentHint = null)
    {
        if (parentSlug == "immobilier")
        {
            if (SearchTermExpander.NormalizedContainsAny(normalized, "studio"))
                return IsSaleContext(normalized, intentHint) ? "studio_a_vendre" : "studio_a_louer";
            if (SearchTermExpander.NormalizedContainsAny(normalized, "maison", "house", "villa", "ndako"))
                return IsSaleContext(normalized, intentHint) ? "maison_a_vendre" : "maison_a_louer";
            if (SearchTermExpander.NormalizedContainsAny(normalized, "appartement", "appartements", "flat", "apartment"))
                return IsSaleContext(normalized, intentHint) ? "appartement_a_vendre" : "appartement_a_louer";
            if (SearchTermExpander.NormalizedContainsAny(normalized, "parcelle", "terrain", "land", "plot"))
                return "parcelle";
            if (SearchTermExpander.NormalizedContainsAny(normalized, "bureau", "commercial", "office"))
                return "bureau";
        }

        if (parentSlug == "vehicules")
        {
            if (SearchTermExpander.NormalizedContainsAny(normalized, "moto", "scooter", "yamaha", "motorcycle"))
                return "moto";
            if (SearchTermExpander.NormalizedContainsAny(normalized, "camion", "truck", "bus", "van"))
                return "camion";
            if (SearchTermExpander.NormalizedContainsAny(normalized, "voiture", "automobile", "toyota", "jeep", "4x4", "car", "motuka"))
                return "voiture";
        }

        if (parentSlug == "telephones" && SearchTermExpander.NormalizedContainsAny(normalized, "tablette", "tablet", "ipad"))
            return "telephone";

        if (parentSlug == "informatique" && SearchTermExpander.NormalizedContainsAny(normalized, "imprimante", "printer"))
            return "informatique";

        if (parentSlug == "emplois")
            return "offre_emploi";

        return null;
    }

    private static bool IsSaleContext(string normalized, string? intentHint) =>
        intentHint == SearchIntentHelper.Offre
        || SearchTermExpander.QueryMatchesConcept(normalized, SearchConcept.Sale);

    private static string? InferParentSlug(string normalized)
    {
        foreach (var (slug, concept) in ParentConcepts)
        {
            if (SearchTermExpander.QueryMatchesConcept(normalized, concept))
                return slug;
        }

        return null;
    }

    private static readonly (string Slug, SearchConcept Concept)[] ParentConcepts =
    [
        ("immobilier", SearchConcept.Immobilier),
        ("vehicules", SearchConcept.Vehicules),
        ("telephones", SearchConcept.Telephones),
        ("informatique", SearchConcept.Informatique),
        ("electronique", SearchConcept.Electronique),
        ("emplois", SearchConcept.Emplois),
        ("meubles", SearchConcept.Meubles),
        ("services", SearchConcept.Services),
        ("mode", SearchConcept.Mode),
        ("jouets", SearchConcept.Jouets),
    ];

    private static bool NamesExplicitSubcategory(string normalized, string parentSlug) =>
        parentSlug switch
        {
            "immobilier" => SearchTermExpander.NormalizedContainsAny(
                normalized,
                "studio",
                "maison",
                "house",
                "villa",
                "ndako",
                "parcelle",
                "terrain",
                "land",
                "bureau",
                "office"),
            "vehicules" => SearchTermExpander.NormalizedContainsAny(
                normalized,
                "moto",
                "scooter",
                "yamaha",
                "motorcycle",
                "voiture",
                "car",
                "motuka",
                "automobile",
                "toyota",
                "jeep",
                "4x4",
                "camion",
                "truck",
                "bus",
                "van"),
            "telephones" => SearchTermExpander.NormalizedContainsAny(normalized, "tablette", "tablet", "ipad"),
            "informatique" => SearchTermExpander.NormalizedContainsAny(normalized, "imprimante", "printer"),
            _ => false,
        };

    private static IReadOnlyList<string> ExtractLocationTerms(string normalized)
    {
        var matches = new List<string>();
        foreach (var commune in KinshasaCommunes.OrderByDescending(c => c.Length))
        {
            if (normalized.Contains(commune, StringComparison.Ordinal))
                matches.Add(commune);
        }

        foreach (var (token, label) in CityTerms)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
                matches.Add(label);
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(NormalizeLocationLabel)
            .ToList();
    }

    private static string NormalizeLocationLabel(string commune) =>
        commune switch
        {
            "bandal" => "Bandal",
            "bandalungwa" => "Bandalungwa",
            "kasa vubu" => "Kasa-Vubu",
            "mont ngafula" => "Mont Ngafula",
            "ndjili" => "Ndjili",
            "Kinshasa" => "Kinshasa",
            "Lubumbashi" => "Lubumbashi",
            _ => char.ToUpperInvariant(commune[0]) + commune[1..],
        };

    private static string RemoveLocations(string normalized, IReadOnlyList<string> locationTerms)
    {
        var result = normalized;
        foreach (var location in locationTerms)
        {
            result = result.Replace(SearchTextNormalizer.Normalize(location), " ", StringComparison.Ordinal);
        }

        return string.Join(' ', result.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
