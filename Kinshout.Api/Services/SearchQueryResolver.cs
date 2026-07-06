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
        if (string.IsNullOrWhiteSpace(query))
            return new SearchQueryHints();

        var normalized = SearchTextNormalizer.Normalize(query);
        if (normalized.Length < 2)
            return new SearchQueryHints();

        var locationTerms = ExtractLocationTerms(normalized);
        var textWithoutLocations = RemoveLocations(normalized, locationTerms);
        var subcategoryText = string.IsNullOrWhiteSpace(textWithoutLocations) ? normalized : textWithoutLocations;
        var parentSlug = InferParentSlug(subcategoryText) ?? InferParentSlug(normalized);

        string? subcategory = null;
        if (parentSlug is not null)
        {
            var inferred = InferSubcategorySlug(subcategoryText, parentSlug);
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

    internal static string? InferSubcategorySlug(string normalized, string parentSlug)
    {
        if (parentSlug == "immobilier")
        {
            if (ContainsAny(normalized, "studio"))
                return ContainsAny(normalized, "vendre", "vente", "sale") ? "studio_a_vendre" : "studio_a_louer";
            if (ContainsAny(normalized, "maison", "villa"))
                return ContainsAny(normalized, "vendre", "vente", "sale") ? "maison_a_vendre" : "maison_a_louer";
            if (ContainsAny(normalized, "appartement", "appartements", "flat", "apartment"))
                return ContainsAny(normalized, "vendre", "vente", "sale") ? "appartement_a_vendre" : "appartement_a_louer";
            if (ContainsAny(normalized, "parcelle", "terrain"))
                return "parcelle";
            if (ContainsAny(normalized, "bureau", "commercial"))
                return "bureau";
        }

        if (parentSlug == "vehicules")
        {
            if (ContainsAny(normalized, "moto", "scooter", "yamaha"))
                return "moto";
            if (ContainsAny(normalized, "camion", "bus"))
                return "camion";
            if (ContainsAny(normalized, "voiture", "automobile", "toyota", "jeep", "4x4"))
                return "voiture";
        }

        if (parentSlug == "telephones" && ContainsAny(normalized, "tablette", "ipad"))
            return "telephone";

        if (parentSlug == "informatique" && ContainsAny(normalized, "imprimante", "printer"))
            return "informatique";

        if (parentSlug == "emplois")
            return "offre_emploi";

        return null;
    }

    private static string? InferParentSlug(string normalized)
    {
        if (ContainsAny(normalized, "appartement", "appartements", "studio", "maison", "villa", "immobilier", "parcelle", "terrain", "ndako", "kofanda"))
            return "immobilier";

        if (ContainsAny(normalized, "voiture", "moto", "vehicule", "automobile", "toyota", "camion", "motuka"))
            return "vehicules";

        if (ContainsAny(normalized, "iphone", "samsung", "telephone", "smartphone", "tablette", "infinix", "tecno"))
            return "telephones";

        if (ContainsAny(normalized, "ordinateur", "macbook", "laptop", "pc ", " pc", "informatique"))
            return "informatique";

        if (ContainsAny(normalized, "electronique", "starlink", "generateur", "groupe electrogene", "panneau solaire", "playstation", "xbox"))
            return "electronique";

        if (ContainsAny(normalized, "emploi", "emplois", "travail", "job", "recrutement", "cv ", " cv", "stage", "mosala"))
            return "emplois";

        if (ContainsAny(normalized, "meuble", "canape", "decoration", "electromenager"))
            return "meubles";

        if (ContainsAny(normalized, "service", "plomberie", "nettoyage", "demenagement", "renovation"))
            return "services";

        if (ContainsAny(normalized, "vetement", "habit", "chaussure", "mode", "montre", "bijou", "sac "))
            return "mode";

        if (ContainsAny(normalized, "jouet", "jeu ", " jeu", "loisir"))
            return "jouets";

        return null;
    }

    private static bool NamesExplicitSubcategory(string normalized, string parentSlug) =>
        parentSlug switch
        {
            "immobilier" => ContainsAny(normalized, "studio", "maison", "villa", "parcelle", "terrain", "bureau"),
            "vehicules" => ContainsAny(
                normalized,
                "moto",
                "scooter",
                "yamaha",
                "voiture",
                "automobile",
                "toyota",
                "jeep",
                "4x4",
                "camion",
                "bus",
                "motuka"),
            "telephones" => ContainsAny(normalized, "tablette", "ipad"),
            "informatique" => ContainsAny(normalized, "imprimante", "printer"),
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

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
