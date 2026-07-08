namespace Kinshout.Api.Services;

using Kinshout.Api.Dtos;

internal static class SearchDiscussionScope
{
    public static bool ShouldSearchDiscussions(SearchRequestDto request, SearchQueryHints hints, string query)
    {
        _ = hints;
        _ = query;

        if (request.Tab.Equals("discussions", StringComparison.OrdinalIgnoreCase))
            return true;

        if (request.Tab.Equals("annonces", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(request.Intent)
            && request.Intent is SearchIntentHelper.Offre or SearchIntentHelper.Demande)
            return false;

        return true;
    }
}
