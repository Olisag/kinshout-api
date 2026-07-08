namespace Kinshout.Api.Services;

internal static class SearchQueryComplexity
{
    private static readonly string[] LingalaDemandColloquial =
    [
        "nazo luka", "nazo koluka", "nazo kolinga", "nazo lingi",
    ];

    private static readonly string[] PriceQualifiers =
    [
        "pas cher", "pas chere", "tres cher", "très cher", "budget", "abordable", "bon prix", "moins cher",
        "cheap", "affordable", "low price", "mbongo moke",
    ];

    public static bool ShouldUseAiUnderstanding(ParsedSearchQuery parsed, SearchQueryHints localHints)
    {
        if (parsed.MatchedPattern)
            return false;

        var normalized = SearchTextNormalizer.Normalize(parsed.OriginalQuery);
        if (normalized.Length < 2)
            return false;

        var rawTerms = SearchTermExpander.ExtractRawTerms(parsed.OriginalQuery);
        if (rawTerms.Count <= 1)
            return false;

        if (rawTerms.Count <= 2 && localHints.HasStructuredFilters)
            return false;

        if (ContainsAny(normalized, LingalaDemandColloquial))
            return true;

        if (ContainsAny(normalized, PriceQualifiers) && localHints.ParentCategorySlug is not null)
            return true;

        if (rawTerms.Count >= 5)
            return true;

        if (rawTerms.Count >= 3 && !localHints.HasStructuredFilters)
            return true;

        return false;
    }

    private static bool ContainsAny(string normalized, IEnumerable<string> needles)
    {
        foreach (var needle in needles)
        {
            if (normalized.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
