using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

internal static class SearchRelevance
{
    /// <summary>
    /// Broad marketplace words that match many unrelated listings when used alone in retrieval.
    /// Strict multi-term relevance applies only when these appear in the parsed subject.
    /// </summary>
    private static readonly string[] GenericMarketplaceTerms =
    [
        "service", "services", "offre", "offres",
    ];

    public static IReadOnlyList<string> CoreSubjectTerms(string? query, SearchQueryHints? hints = null)
    {
        if (hints?.RetrievalTerms is { Count: > 0 })
        {
            return hints.RetrievalTerms
                .Where(term => term.Length >= 3 && !SearchTermExpander.IsRetrievalStopWord(term))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var subject = !string.IsNullOrWhiteSpace(hints?.SubjectText)
            ? hints.SubjectText
            : SearchQueryParser.Parse(query).SubjectText;

        if (string.IsNullOrWhiteSpace(query))
            return [];

        var raw = SearchTermExpander.ExtractRawTerms(subject);
        if (raw.Count == 0)
            raw = SearchTermExpander.ExtractRawTerms(query);

        return raw;
    }

    public static bool IsDiscussionRelevant(string query, Discussion discussion, SearchQueryHints? hints = null)
    {
        var coreTerms = CoreSubjectTerms(query, hints);
        if (coreTerms.Count == 0)
            return true;

        var title = SearchTextNormalizer.Normalize(discussion.Title);
        var body = SearchTextNormalizer.Normalize(discussion.Body);
        return HasRequiredCoreTermMatches(coreTerms, title, body);
    }

    public static bool IsAdvertRelevant(string query, Advert advert, SearchQueryHints? hints = null)
    {
        var coreTerms = CoreSubjectTerms(query, hints);
        if (coreTerms.Count == 0 || !RequiresStrictAdvertRelevance(coreTerms))
            return true;

        var title = SearchTextNormalizer.Normalize(advert.Title);
        var description = SearchTextNormalizer.Normalize(advert.Description);
        var location = SearchTextNormalizer.Normalize(advert.Location);
        var tags = SearchTextNormalizer.Normalize(advert.TagsJson);
        var subcategory = SearchTextNormalizer.Normalize(advert.SubcategorySlug);
        var category = SearchTextNormalizer.Normalize(advert.Category?.Label);
        var combined = $"{title} {description} {location} {tags} {subcategory} {category}";

        return HasRequiredCoreTermMatches(coreTerms, combined);
    }

    private static bool RequiresStrictAdvertRelevance(IReadOnlyList<string> coreTerms) =>
        coreTerms.Any(term => GenericMarketplaceTerms.Contains(term));

    private static bool HasRequiredCoreTermMatches(
        IReadOnlyList<string> coreTerms,
        string primaryField,
        string? secondaryField = null)
    {
        var matched = coreTerms.Count(term =>
            MatchesWholeTerm(primaryField, term)
            || (secondaryField is not null && MatchesWholeTerm(secondaryField, term)));

        return matched >= RequiredCoreTermMatches(coreTerms);
    }

    public static int RequiredCoreTermMatches(IReadOnlyList<string> coreTerms) =>
        coreTerms.Count switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            _ => Math.Min(coreTerms.Count, Math.Max(2, (int)Math.Ceiling(coreTerms.Count * 0.6))),
        };

    public static bool MatchesWholeTerm(string field, string term)
    {
        if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(term))
            return false;

        var index = 0;
        while ((index = field.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetter(field[index - 1]);
            var afterIndex = index + term.Length;
            var afterOk = afterIndex >= field.Length || !char.IsLetter(field[afterIndex]);
            if (beforeOk && afterOk)
                return true;

            index += term.Length;
        }

        return false;
    }
}
