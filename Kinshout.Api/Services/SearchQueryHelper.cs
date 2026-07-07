using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

public static partial class SearchQueryHelper
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "à", "au", "aux", "de", "du", "des", "d", "le", "la", "les", "l",
        "un", "une", "et", "en", "pour", "par", "sur", "dans", "avec", "chez",
        "je", "tu", "il", "elle", "on", "nous", "vous", "ils", "elles", "me", "te", "se",
        "mon", "ma", "mes", "ton", "ta", "tes", "son", "sa", "ses", "notre", "votre", "leur",
        "ce", "cet", "cette", "ces", "qui", "que", "quoi", "dont", "où",
        "cherche", "chercher", "recherche", "rechercher", "trouver", "trouve",
        "est", "sont", "ai", "as", "avons", "avez", "ont",
    };

    private static readonly Dictionary<string, string> TokenAliases = new(StringComparer.Ordinal)
    {
        ["appart"] = "appartement",
        ["apparts"] = "appartement",
        ["apt"] = "appartement",
        ["apts"] = "appartement",
        ["appartement"] = "appartement",
        ["appartements"] = "appartement",
        ["studio"] = "appartement",
        ["studios"] = "appartement",
        ["loc"] = "location",
        ["location"] = "location",
        ["locations"] = "location",
        ["louer"] = "location",
        ["loue"] = "location",
        ["louee"] = "location",
        ["louees"] = "location",
        ["loues"] = "location",
        ["loué"] = "location",
        ["louée"] = "location",
        ["loués"] = "location",
        ["louées"] = "location",
        ["vendre"] = "vente",
        ["vends"] = "vente",
        ["vente"] = "vente",
        ["ventes"] = "vente",
        ["moto"] = "moto",
        ["motos"] = "moto",
        ["voiture"] = "voiture",
        ["voitures"] = "voiture",
        ["auto"] = "voiture",
        ["autos"] = "voiture",
        ["job"] = "emploi",
        ["jobs"] = "emploi",
        ["emploi"] = "emploi",
        ["emplois"] = "emploi",
        ["travail"] = "emploi",
        ["iphone"] = "iphone",
        ["iphones"] = "iphone",
        ["macbook"] = "macbook",
        ["macbooks"] = "macbook",
        ["starlink"] = "starlink",
        ["discussion"] = "discussion",
        ["discussions"] = "discussion",
        ["generateur"] = "generateur",
        ["générateur"] = "generateur",
        ["generateurs"] = "generateur",
        ["solaire"] = "solaire",
        ["solaires"] = "solaire",
        ["chauffeur"] = "chauffeur",
        ["chauffeurs"] = "chauffeur",
        ["vtc"] = "vtc",
        ["maths"] = "mathematique",
        ["mathematiques"] = "mathematique",
        ["mathematique"] = "mathematique",
        ["cours"] = "cours",
        ["particuliers"] = "particulier",
        ["particulier"] = "particulier",
        ["maison"] = "maison",
        ["maisons"] = "maison",
        ["kinshasa"] = "kinshasa",
        ["kin"] = "kinshasa",
        ["gombe"] = "gombe",
        ["bandal"] = "bandal",
        ["limete"] = "limete",
        ["binza"] = "binza",
    };

    public static string? Normalize(string? query) => CanonicalKey(query);

    public static string? CanonicalKey(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var cleaned = RemoveDiacritics(query.Trim().ToLowerInvariant());
        cleaned = NonWord().Replace(cleaned, " ");
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        if (cleaned.Length < 2)
            return null;

        var tokens = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeToken)
            .Where(IsMeaningfulToken)
            .Where(t => !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    private static bool IsMeaningfulToken(string token) =>
        token.Length >= 2 || token.All(char.IsDigit);

    public static string Display(string query) =>
        Whitespace().Replace(query.Trim(), " ");

    public static string CanonicalizeToken(string token)
    {
        token = RemoveDiacritics(token.ToLowerInvariant());
        if (token.Length <= 2)
            return token;

        token = Singularize(token);
        return TokenAliases.TryGetValue(token, out var alias) ? alias : token;
    }

    private static string NormalizeToken(string token)
    {
        token = RemoveDiacritics(token.ToLowerInvariant());
        if (token.Length <= 2)
            return token;

        return SearchSpellingNormalizer.CanonicalizeToken(token);
    }

    private static string Singularize(string token)
    {
        if (TokenAliases.ContainsKey(token))
            return token;

        if (token.Length <= 3 || token.EndsWith("ss", StringComparison.Ordinal))
            return token;

        if (token.EndsWith('s') && !token.EndsWith("us", StringComparison.Ordinal))
            return token[..^1];

        return token;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
