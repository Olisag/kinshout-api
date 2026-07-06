using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class SearchMatchHelper
{
    private const int TitleWeight = 10;
    private const int BodyWeight = 5;
    private const int LocationWeight = 3;
    private const int TagsWeight = 2;
    private const int SubcategoryWeight = 2;
    private const int CategoryWeight = 2;
    private const int PhraseBonus = 15;
    private const int PopularityDivisor = 1_000;

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
        var advertIds = RankAdvertIds(query, adverts);
        var discussionIds = RankDiscussionIds(query, discussions);
        return new AiSearchAnalysis(advertIds, discussionIds, $"Résultats pour « {query} ».");
    }

    public static IReadOnlyList<Guid> RankAdvertIds(string query, IReadOnlyList<Advert> adverts)
    {
        var terms = ExtractTerms(query);
        var normalizedQuery = SearchTextNormalizer.Normalize(query);
        return adverts
            .Select(a => new RankedItem(a.Id, ScoreAdvert(terms, normalizedQuery, a), a.ViewCount, AdvertSourceMapper.SortDate(a)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.ViewCount / PopularityDivisor)
            .ThenByDescending(x => x.SortDate)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();
    }

    public static IReadOnlyList<Guid> RankDiscussionIds(string query, IReadOnlyList<Discussion> discussions)
    {
        var terms = ExtractTerms(query);
        var normalizedQuery = SearchTextNormalizer.Normalize(query);
        return discussions
            .Select(d => new RankedItem(
                d.Id,
                ScoreDiscussion(terms, normalizedQuery, d),
                d.ViewCount,
                d.CreatedAt))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.ViewCount / PopularityDivisor)
            .ThenByDescending(x => x.SortDate)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();
    }

    internal static int ScoreAdvert(IReadOnlyList<string> terms, string normalizedQuery, Advert advert)
    {
        var title = SearchTextNormalizer.Normalize(advert.Title);
        var description = SearchTextNormalizer.Normalize(advert.Description);
        var location = SearchTextNormalizer.Normalize(advert.Location);
        var tags = SearchTextNormalizer.Normalize(advert.TagsJson);
        var subcategory = SearchTextNormalizer.Normalize(advert.SubcategorySlug);
        var category = SearchTextNormalizer.Normalize(advert.Category?.Label);

        var score = ScoreFields(terms, title, description, location, tags, subcategory, category);
        if (!string.IsNullOrEmpty(normalizedQuery) && title.Contains(normalizedQuery, StringComparison.Ordinal))
            score += PhraseBonus;

        return score;
    }

    internal static int ScoreDiscussion(IReadOnlyList<string> terms, string normalizedQuery, Discussion discussion)
    {
        var title = SearchTextNormalizer.Normalize(discussion.Title);
        var body = SearchTextNormalizer.Normalize(discussion.Body);
        var category = SearchTextNormalizer.Normalize(discussion.Category?.Label);
        var topic = SearchTextNormalizer.Normalize(discussion.TopicSlug);

        var score = ScoreFields(terms, title, body, location: "", tags: topic, subcategory: "", category);
        if (!string.IsNullOrEmpty(normalizedQuery) && title.Contains(normalizedQuery, StringComparison.Ordinal))
            score += PhraseBonus;

        return score;
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

    private static int ScoreFields(
        IReadOnlyList<string> terms,
        string title,
        string body,
        string location,
        string tags,
        string subcategory,
        string category)
    {
        if (terms.Count == 0)
        {
            var combined = $"{title} {body} {location} {tags} {subcategory} {category}";
            return string.IsNullOrWhiteSpace(combined) ? 0 : 1;
        }

        var score = 0;
        foreach (var term in terms)
        {
            if (ContainsTerm(title, term))
                score += TitleWeight;
            if (ContainsTerm(body, term))
                score += BodyWeight;
            if (ContainsTerm(location, term))
                score += LocationWeight;
            if (ContainsTerm(tags, term))
                score += TagsWeight;
            if (ContainsTerm(subcategory, term))
                score += SubcategoryWeight;
            if (ContainsTerm(category, term))
                score += CategoryWeight;
        }

        return score;
    }

    private static bool ContainsTerm(string field, string term) =>
        !string.IsNullOrEmpty(field) && field.Contains(term, StringComparison.Ordinal);

    private readonly record struct RankedItem(Guid Id, int Score, int ViewCount, DateTime SortDate);
}
