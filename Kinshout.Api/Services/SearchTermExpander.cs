namespace Kinshout.Api.Services;

internal enum SearchConcept
{
    Immobilier,
    Vehicules,
    Telephones,
    Informatique,
    Electronique,
    Emplois,
    Meubles,
    Services,
    Mode,
    Jouets,
    Rent,
    Sale,
    Search,
}

internal static class SearchTermExpander
{
    public const int MaxExpandedTerms = 12;

    private static readonly string[] FrenchStopWords =
    [
        "avec", "dans", "des", "les", "pour", "une", "sur", "par", "est", "son", "ses", "aux", "mon", "ma",
    ];

    private static readonly string[] EnglishStopWords =
    [
        "the", "and", "for", "with", "from", "are", "was", "has", "have", "this", "that",
    ];

    private static readonly string[] LingalaStopWords =
    [
        "mpe", "pe", "oyo", "yango", "soki", "kasi", "nazali", "na",
    ];

    /// <summary>
    /// Demand / intent words — describe what the user wants to do, not what they search for.
    /// Must not be used as SQL retrieval terms (e.g. "cherche" matches almost everything).
    /// </summary>
    private static readonly string[] DemandStopWords =
    [
        "cherche", "cherches", "cherchent", "recherche", "recherches", "recherchons", "besoin", "veux", "veut",
        "voulez", "voulons", "achete", "acheter", "achetes", "recrute", "recrutons",
        "looking", "search", "searching", "need", "needs", "want", "wants", "buy", "buying", "hire", "hiring",
        "koluka", "kolingi", "nalingi", "kolinga",
    ];

    /// <summary>
    /// Sale / rent boilerplate — intent context only, not product keywords.
    /// Must not be used as SQL retrieval terms (e.g. "vente" matches almost every listing).
    /// </summary>
    private static readonly string[] OfferStopWords =
    [
        "offre", "offres", "propose", "proposition", "propositions",
        "vente", "ventes", "vendre", "vends", "vend", "vendu", "vendue",
        "sale", "sell", "selling", "sold",
        "koteka", "kotia", "kotisa",
        "louer", "loue", "location", "rent", "rental", "lease",
    ];

    private static readonly Dictionary<string, SearchConcept> ConceptBySlug = new(StringComparer.Ordinal)
    {
        ["immobilier"] = SearchConcept.Immobilier,
        ["vehicules"] = SearchConcept.Vehicules,
        ["telephones"] = SearchConcept.Telephones,
        ["informatique"] = SearchConcept.Informatique,
        ["electronique"] = SearchConcept.Electronique,
        ["emplois"] = SearchConcept.Emplois,
        ["meubles"] = SearchConcept.Meubles,
        ["services"] = SearchConcept.Services,
        ["mode"] = SearchConcept.Mode,
        ["jouets"] = SearchConcept.Jouets,
    };

    private static readonly Dictionary<SearchConcept, string[]> ConceptTerms = BuildConceptTerms();

    private static readonly string[][] SynonymGroups =
    [
        ["appartement", "appartements", "apartment", "apartments", "flat", "flats"],
        ["maison", "house", "villa", "ndako", "kofanda"],
        ["studio", "kotiya"],
        ["terrain", "parcelle", "land", "plot"],
        ["louer", "location", "rent", "rental", "for rent", "to rent", "lease"],
        ["vendre", "vente", "sale", "sell", "selling", "for sale", "koteka", "kotia", "kotisa"],
        ["voiture", "car", "cars", "automobile", "motuka", "vehicle", "vehicule"],
        ["moto", "motorcycle", "scooter", "motorbike"],
        ["camion", "truck", "bus", "van"],
        ["telephone", "telephones", "phone", "phones", "smartphone", "simu", "mobile"],
        ["ordinateur", "computer", "laptop", "macbook", "pc"],
        ["tablette", "tablet", "ipad"],
        ["emploi", "emplois", "job", "jobs", "work", "travail", "mosala", "kozwa mosala", "recrutement", "stage", "cv"],
        ["meuble", "meubles", "furniture", "canape", "sofa", "decoration", "electromenager"],
        ["service", "services", "plomberie", "nettoyage", "demenagement", "renovation"],
        ["vetement", "vetements", "clothes", "clothing", "habit"],
        ["chaussure", "shoes"],
        ["mode", "montre", "watch", "bijou", "sac", "bag"],
        ["enfant", "enfants", "kids", "children", "bebe", "bebes"],
        ["jouet", "jouets", "toy", "toys", "jeu", "game", "loisir"],
        ["electronique", "starlink", "generateur", "groupe electrogene", "panneau solaire", "playstation", "xbox", "console"],
        ["iphone", "samsung", "infinix", "tecno"],
        ["imprimante", "printer"],
        ["immobilier", "real estate", "property", "housing"],
        ["mbongo", "money", "argent", "price", "prix", "budget"],
    ];

    private static readonly Dictionary<string, HashSet<string>> TermSynonyms = BuildTermSynonyms();

    internal static IEnumerable<string> GetVocabularyTerms()
    {
        foreach (var group in SynonymGroups)
        {
            foreach (var term in group)
                yield return term;
        }

        foreach (var terms in ConceptTerms.Values)
        {
            foreach (var term in terms)
                yield return term;
        }
    }

    public static IReadOnlyList<string> ExtractExpandedTerms(string query)
    {
        var parsed = SearchQueryParser.Parse(query);
        var subject = parsed.SubjectText;
        var raw = ExtractRawTerms(subject);
        if (raw.Count == 0 && !string.Equals(subject, parsed.OriginalQuery, StringComparison.Ordinal))
            raw = ExtractRawTerms(parsed.OriginalQuery);
        return Expand(raw);
    }

    public static bool QueryMatchesConcept(string query, SearchConcept concept)
    {
        if (!ConceptTerms.TryGetValue(concept, out var seeds))
            return false;

        var normalized = SearchSpellingNormalizer.CanonicalizeText(query);
        foreach (var seed in seeds)
        {
            if (normalized.Contains(seed, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool QueryMatchesParentSlug(string query, string parentSlug) =>
        ConceptBySlug.TryGetValue(parentSlug, out var concept) && QueryMatchesConcept(query, concept);

    public static bool NormalizedContainsAny(string normalized, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (normalized.Contains(needle, StringComparison.Ordinal))
                return true;

            if (TermSynonyms.TryGetValue(needle, out var synonyms))
            {
                foreach (var synonym in synonyms)
                {
                    if (synonym != needle && normalized.Contains(synonym, StringComparison.Ordinal))
                        return true;
                }
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ExtractRawTerms(string query)
    {
        var normalized = SearchTextNormalizer.Normalize(query);
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SearchSpellingNormalizer.CanonicalizeToken)
            .Where(term => term.Length >= 3 && !IsStopWord(term))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    internal static IReadOnlyList<string> Expand(IEnumerable<string> terms)
    {
        var termList = terms as IList<string> ?? terms.ToList();
        var expanded = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in termList)
            AddTerm(expanded, seen, term);

        foreach (var term in termList)
        {
            if (expanded.Count >= MaxExpandedTerms)
                return expanded;

            if (TermSynonyms.TryGetValue(term, out var synonyms))
            {
                foreach (var synonym in synonyms)
                {
                    AddTerm(expanded, seen, synonym);
                    if (expanded.Count >= MaxExpandedTerms)
                        return expanded;
                }
            }
        }

        return expanded;
    }

    private static void AddTerm(List<string> expanded, HashSet<string> seen, string term)
    {
        if (string.IsNullOrWhiteSpace(term) || !seen.Add(term))
            return;

        expanded.Add(term);
    }

    private static bool IsStopWord(string term) =>
        FrenchStopWords.Contains(term)
        || EnglishStopWords.Contains(term)
        || LingalaStopWords.Contains(term)
        || DemandStopWords.Contains(term)
        || OfferStopWords.Contains(term);

    private static Dictionary<string, HashSet<string>> BuildTermSynonyms()
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var group in SynonymGroups)
        {
            var set = new HashSet<string>(group, StringComparer.Ordinal);
            foreach (var term in group)
                lookup[term] = set;
        }

        return lookup;
    }

    private static Dictionary<SearchConcept, string[]> BuildConceptTerms() =>
        new()
        {
            [SearchConcept.Immobilier] =
            [
                "appartement", "appartements", "apartment", "apartments", "flat", "flats", "studio", "maison", "house",
                "villa", "immobilier", "parcelle", "terrain", "ndako", "kofanda", "kotiya", "property", "housing", "rent",
                "louer", "location",
            ],
            [SearchConcept.Vehicules] =
            [
                "voiture", "car", "cars", "moto", "motorcycle", "scooter", "vehicule", "vehicle", "automobile", "toyota",
                "camion", "truck", "bus", "motuka", "jeep", "4x4", "yamaha",
            ],
            [SearchConcept.Telephones] =
            [
                "iphone", "samsung", "telephone", "telephones", "phone", "phones", "smartphone", "tablette", "tablet",
                "ipad", "infinix", "tecno", "simu", "mobile",
            ],
            [SearchConcept.Informatique] =
            [
                "ordinateur", "computer", "laptop", "macbook", "pc", "informatique", "imprimante", "printer",
            ],
            [SearchConcept.Electronique] =
            [
                "electronique", "starlink", "generateur", "groupe electrogene", "panneau solaire", "playstation", "xbox",
                "console", "tv", "modem", "wifi", "internet",
            ],
            [SearchConcept.Emplois] =
            [
                "emploi", "emplois", "travail", "job", "jobs", "work", "recrutement", "cv", "stage", "mosala",
                "kozwa mosala",
            ],
            [SearchConcept.Meubles] =
            [
                "meuble", "meubles", "furniture", "canape", "sofa", "decoration", "electromenager", "frigo",
                "refrigerator",
            ],
            [SearchConcept.Services] =
            [
                "service", "services", "plomberie", "nettoyage", "demenagement", "renovation", "electrician",
                "electricien", "plombier",
            ],
            [SearchConcept.Mode] =
            [
                "vetement", "vetements", "clothes", "clothing", "habit", "chaussure", "shoes", "mode", "montre", "watch",
                "bijou", "jewelry", "sac", "bag", "beaute", "beauty", "enfant", "enfants", "kids", "children", "bebe", "bebes",
            ],
            [SearchConcept.Jouets] =
            [
                "jouet", "jouets", "toy", "toys", "jeu", "game", "games", "loisir",
            ],
            [SearchConcept.Rent] =
            [
                "louer", "location", "rent", "rental", "for rent", "to rent", "lease", "kofanda",
            ],
            [SearchConcept.Sale] =
            [
                "vendre", "vente", "sale", "sell", "selling", "for sale", "koteka", "kotia", "kotisa",
            ],
            [SearchConcept.Search] =
            [
                "cherche", "recherche", "search", "looking", "need", "want", "koluka", "kolinga", "nalingi",
            ],
        };
}
