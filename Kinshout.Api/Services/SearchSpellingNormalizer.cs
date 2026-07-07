namespace Kinshout.Api.Services;

internal static class SearchSpellingNormalizer
{
    private static readonly Dictionary<string, string> KnownCorrections = new(StringComparer.Ordinal)
    {
        ["apartement"] = "appartement",
        ["apartements"] = "appartement",
        ["appartament"] = "appartement",
        ["appartaments"] = "appartement",
        ["appertement"] = "appartement",
        ["appertements"] = "appartement",
        ["appart"] = "appartement",
        ["apparts"] = "appartement",
        ["apartment"] = "appartement",
        ["apartments"] = "appartement",
        ["vehicule"] = "voiture",
        ["vehicules"] = "voiture",
        ["motorcycle"] = "moto",
        ["motocycle"] = "moto",
        ["telefone"] = "telephone",
        ["telefones"] = "telephone",
        ["ordianteur"] = "ordinateur",
        ["ordinateurs"] = "ordinateur",
        ["immobilier"] = "immobilier",
        ["immobillier"] = "immobilier",
        ["electromenager"] = "electromenager",
        ["electomenager"] = "electromenager",
    };

    private static readonly HashSet<string> Vocabulary = BuildVocabulary();

    public static string CanonicalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = SearchTextNormalizer.Normalize(text);
        return string.Join(
            ' ',
            normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(CanonicalizeToken));
    }

    public static string CanonicalizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var normalized = token.ToLowerInvariant();
        if (normalized.Length < 3)
            return normalized;

        var aliased = SearchQueryHelper.CanonicalizeToken(normalized);
        if (!string.Equals(aliased, normalized, StringComparison.Ordinal))
            return aliased;

        if (KnownCorrections.TryGetValue(normalized, out var known))
            return known;

        if (Vocabulary.Contains(normalized))
            return normalized;

        return FuzzyMatch(normalized) ?? normalized;
    }

    private static string? FuzzyMatch(string token)
    {
        var maxDistance = token.Length switch
        {
            < 6 => 0,
            < 9 => 1,
            _ => 2,
        };

        if (maxDistance == 0)
            return null;

        string? best = null;
        var bestDistance = maxDistance + 1;

        foreach (var candidate in Vocabulary)
        {
            if (Math.Abs(candidate.Length - token.Length) > maxDistance)
                continue;

            var distance = LevenshteinDistance(token, candidate);
            if (distance > maxDistance || distance >= bestDistance)
                continue;

            best = candidate;
            bestDistance = distance;
            if (distance == 0)
                break;
        }

        return best;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static HashSet<string> BuildVocabulary()
    {
        var vocabulary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in SearchTermExpander.GetVocabularyTerms())
        {
            if (term.Length >= 4 && !term.Contains(' ', StringComparison.Ordinal))
                vocabulary.Add(term);
        }

        foreach (var term in KnownCorrections.Values)
            vocabulary.Add(term);

        return vocabulary;
    }
}
