using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

internal static class SearchQueryUnderstanding
{
    private const string CacheKeyPrefix = "search:query-understanding:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public static async Task<SearchQueryHints> ResolveAsync(
        string query,
        IOpenAiService openAi,
        IMemoryCache cache,
        CancellationToken ct)
    {
        var parsed = SearchQueryParser.Parse(query);
        var localHints = SearchQueryResolver.ParseHints(parsed);

        if (!SearchQueryComplexity.ShouldUseAiUnderstanding(parsed, localHints))
            return localHints;

        var normalizedKey = SearchQueryHelper.Normalize(query);
        if (!string.IsNullOrWhiteSpace(normalizedKey)
            && cache.TryGetValue<AiSearchQueryAnalysis>(CacheKeyPrefix + normalizedKey, out var cached)
            && cached is not null)
        {
            return Merge(localHints, cached);
        }

        var ai = await openAi.AnalyzeSearchQueryAsync(query, ct);
        if (!ai.RuleBasedFallback && ai.Confidence >= 0.5 && !string.IsNullOrWhiteSpace(normalizedKey))
            cache.Set(CacheKeyPrefix + normalizedKey, ai, CacheDuration);

        return Merge(localHints, ai);
    }

    internal static SearchQueryHints Merge(SearchQueryHints local, AiSearchQueryAnalysis ai)
    {
        if (ai.RuleBasedFallback)
            return local;

        var retrievalTerms = NormalizeRetrievalTerms(ai.RetrievalTerms, ai.SubjectText);
        return new SearchQueryHints
        {
            LocationTerms = ai.LocationTerms.Count > 0 ? ai.LocationTerms : local.LocationTerms,
            SubcategorySlug = ai.SubcategorySlug ?? local.SubcategorySlug,
            ParentCategorySlug = ai.ParentCategorySlug ?? local.ParentCategorySlug,
            SubjectText = string.IsNullOrWhiteSpace(ai.SubjectText) ? local.SubjectText : ai.SubjectText,
            IntentHint = ai.IntentHint ?? local.IntentHint,
            RetrievalTerms = retrievalTerms,
            UsedAiUnderstanding = true,
        };
    }

    private static IReadOnlyList<string> NormalizeRetrievalTerms(
        IReadOnlyList<string> retrievalTerms,
        string? subjectText)
    {
        if (retrievalTerms.Count > 0)
        {
            return retrievalTerms
                .Select(term => SearchSpellingNormalizer.CanonicalizeToken(SearchTextNormalizer.Normalize(term)))
                .Where(term => term.Length >= 3 && !SearchTermExpander.IsRetrievalStopWord(term))
                .Distinct(StringComparer.Ordinal)
                .Take(SearchTermExpander.MaxExpandedTerms)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(subjectText))
            return [];

        return SearchTermExpander.ExtractExpandedTerms(subjectText);
    }
}
