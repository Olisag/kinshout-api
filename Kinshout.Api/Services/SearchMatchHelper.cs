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


    public static AiSearchAnalysis Rank(
        string query,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions,
        SearchQueryHints? hints = null)
    {
        var advertIds = RankAdvertIds(query, adverts, hints);
        var discussionIds = RankDiscussionIds(query, discussions, hints);
        return new AiSearchAnalysis(advertIds, discussionIds, $"Résultats pour « {query} ».");
    }

    public static IReadOnlyList<Guid> RankAdvertIds(
        string query,
        IReadOnlyList<Advert> adverts,
        SearchQueryHints? hints = null)
    {
        var terms = ExtractTerms(query, hints);
        var subject = ResolveSubjectText(query, hints);
        var normalizedQuery = SearchTextNormalizer.Normalize(subject);
        return adverts
            .Select(a => new RankedItem(
                a.Id,
                ScoreAdvert(terms, normalizedQuery, a),
                a.ViewCount,
                AdvertSourceMapper.SortDate(a),
                Advert: a))
            .Where(x => x.Score > 0 && SearchRelevance.IsAdvertRelevant(query, x.Advert!, hints))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.ViewCount / PopularityDivisor)
            .ThenByDescending(x => x.SortDate)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();
    }

    public static IReadOnlyList<Guid> RankDiscussionIds(
        string query,
        IReadOnlyList<Discussion> discussions,
        SearchQueryHints? hints = null)
    {
        var terms = ExtractTerms(query, hints);
        var subject = ResolveSubjectText(query, hints);
        var normalizedQuery = SearchTextNormalizer.Normalize(subject);
        return discussions
            .Select(d => new RankedItem(
                d.Id,
                ScoreDiscussion(terms, normalizedQuery, d),
                d.ViewCount,
                d.CreatedAt,
                d))
            .Where(x => x.Score > 0 && SearchRelevance.IsDiscussionRelevant(query, x.Discussion!, hints))
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

    internal static IReadOnlyList<string> ExtractTerms(string query, SearchQueryHints? hints = null)
    {
        if (hints?.RetrievalTerms is { Count: > 0 })
            return SearchTermExpander.Expand(hints.RetrievalTerms);

        var subject = ResolveSubjectText(query, hints);
        return SearchTermExpander.ExtractExpandedTerms(
            string.IsNullOrWhiteSpace(subject) ? query : subject);
    }

    private static string ResolveSubjectText(string query, SearchQueryHints? hints)
    {
        if (!string.IsNullOrWhiteSpace(hints?.SubjectText))
            return SearchSpellingNormalizer.CanonicalizeText(hints.SubjectText);

        return SearchSpellingNormalizer.CanonicalizeText(SearchQueryParser.Parse(query).SubjectText);
    }

    public static bool IsConfidentLocalRank(
        string query,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions)
    {
        var advertScores = RankAdvertScores(query, adverts);
        var discussionScores = RankDiscussionScores(query, discussions);

        if (advertScores.Count > 0 && discussionScores.Count > 0)
            return IsConfidentScoreList(advertScores) && IsConfidentScoreList(discussionScores);

        if (advertScores.Count > 0)
            return IsConfidentScoreList(advertScores);

        if (discussionScores.Count > 0)
            return IsConfidentScoreList(discussionScores);

        return false;
    }

    internal static IReadOnlyList<int> RankAdvertScores(string query, IReadOnlyList<Advert> adverts)
    {
        var terms = ExtractTerms(query);
        var subject = SearchSpellingNormalizer.CanonicalizeText(SearchQueryParser.Parse(query).SubjectText);
        var normalizedQuery = SearchTextNormalizer.Normalize(subject);
        return adverts
            .Select(a => ScoreAdvert(terms, normalizedQuery, a))
            .Where(score => score > 0)
            .OrderByDescending(score => score)
            .ToList();
    }

    internal static IReadOnlyList<int> RankDiscussionScores(string query, IReadOnlyList<Discussion> discussions)
    {
        var terms = ExtractTerms(query);
        var subject = SearchSpellingNormalizer.CanonicalizeText(SearchQueryParser.Parse(query).SubjectText);
        var normalizedQuery = SearchTextNormalizer.Normalize(subject);
        return discussions
            .Select(d => ScoreDiscussion(terms, normalizedQuery, d))
            .Where(score => score > 0)
            .OrderByDescending(score => score)
            .ToList();
    }

    private static bool IsConfidentScoreList(IReadOnlyList<int> scores)
    {
        if (scores.Count == 0)
            return true;

        var top = scores[0];
        if (top >= PhraseBonus + TitleWeight)
            return true;

        if (top < TitleWeight)
            return false;

        if (scores.Count == 1)
            return true;

        var compareIndex = Math.Min(4, scores.Count - 1);
        var compareScore = scores[compareIndex];
        return compareScore == 0 || top >= compareScore * 2;
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
        SearchRelevance.MatchesWholeTerm(field, term);

    private readonly record struct RankedItem(
        Guid Id,
        int Score,
        int ViewCount,
        DateTime SortDate,
        Discussion? Discussion = null,
        Advert? Advert = null);
}
