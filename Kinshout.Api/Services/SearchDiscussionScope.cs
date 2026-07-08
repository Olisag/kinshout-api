namespace Kinshout.Api.Services;

using Kinshout.Api.Dtos;

internal static class SearchDiscussionScope
{
    public static bool ShouldSearchDiscussions(SearchRequestDto request, SearchQueryHints hints, string query)
    {
        if (request.Tab.Equals("discussions", StringComparison.OrdinalIgnoreCase))
            return true;

        if (request.Tab.Equals("annonces", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsDiscussionTopicQuery(query))
            return true;

        var parsed = SearchQueryParser.Parse(query);
        if (parsed.IntentHint == SearchIntentHelper.Offre)
            return false;

        if (!string.IsNullOrWhiteSpace(request.Intent)
            && request.Intent is SearchIntentHelper.Offre or SearchIntentHelper.Demande)
            return false;

        if (!string.IsNullOrWhiteSpace(hints.ParentCategorySlug))
            return false;

        return true;
    }

    private static bool IsDiscussionTopicQuery(string query)
    {
        var subject = SearchQueryParser.Parse(query).SubjectText;
        var normalized = SearchSpellingNormalizer.CanonicalizeText(subject);
        if (normalized.Length < 2)
            normalized = SearchSpellingNormalizer.CanonicalizeText(query);
        if (normalized.Length < 2)
            return false;

        return SearchTermExpander.NormalizedContainsAny(
            normalized,
            "discussion",
            "discussions",
            "debat",
            "forum",
            "topic",
            "sujet",
            "question",
            "avis");
    }
}
