using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class SearchMatchHelper
{
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.Ordinal)
    {
        "avec",
        "dans",
        "des",
        "les",
        "pour",
        "une",
    };

    public static AiSearchAnalysis Rank(
        string query,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions)
    {
        var terms = ExtractTerms(query);
        var advertIds = adverts
            .Select(a => new
            {
                a.Id,
                Score = Score(
                    terms,
                    $"{a.Title} {a.Description} {a.Location} {a.Price} {a.TagsJson ?? ""} {a.SubcategorySlug} {a.Category?.Label}"),
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        var discussionIds = discussions
            .Select(d => new
            {
                d.Id,
                Score = Score(terms, $"{d.Title} {d.Body} {d.Category?.Label} {d.TopicSlug}"),
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        return new AiSearchAnalysis(advertIds, discussionIds, $"Résultats pour « {query} ».");
    }

    internal static IReadOnlyList<string> ExtractTerms(string query)
    {
        var normalized = SearchTextNormalizer.Normalize(query);
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3 && !SearchStopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static int Score(IReadOnlyList<string> terms, string text)
    {
        var normalized = SearchTextNormalizer.Normalize(text);
        if (terms.Count == 0)
            return normalized.Contains(text, StringComparison.Ordinal) ? 1 : 0;

        return terms.Count(term => normalized.Contains(term, StringComparison.Ordinal));
    }
}
