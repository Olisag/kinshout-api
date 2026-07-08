using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

internal static class SearchRelevance
{
    public static IReadOnlyList<string> CoreSubjectTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var parsed = SearchQueryParser.Parse(query);
        var raw = SearchTermExpander.ExtractRawTerms(parsed.SubjectText);
        if (raw.Count == 0)
            raw = SearchTermExpander.ExtractRawTerms(query);

        return raw;
    }

    public static bool IsDiscussionRelevant(string query, Discussion discussion)
    {
        var coreTerms = CoreSubjectTerms(query);
        if (coreTerms.Count == 0)
            return true;

        var title = SearchTextNormalizer.Normalize(discussion.Title);
        var body = SearchTextNormalizer.Normalize(discussion.Body);
        var matched = coreTerms.Count(term =>
            MatchesWholeTerm(title, term) || MatchesWholeTerm(body, term));

        return coreTerms.Count switch
        {
            1 => matched >= 1,
            2 => matched >= 2,
            _ => matched >= Math.Min(coreTerms.Count, Math.Max(2, (int)Math.Ceiling(coreTerms.Count * 0.6))),
        };
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
