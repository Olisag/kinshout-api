using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

/// <summary>
/// Keeps party politics and armed conflict out of public feeds.
/// The same names are blocked at import in KinshasaSocialPostFilter.
/// </summary>
public static class PoliticsFeedFilter
{
    private static readonly string[] Terms =
    [
        "fayulu",
        "kabund",
        "mukebayi",
        "kamerhe",
        "katumbi",
        "tshisekedi",
        "fatshi",
        "kabila",
        "fcc ",
        "udps",
        "m23",
        "fardc",
        "fdlr",
        "topcongo",
        "top congo",
        "rwanda",
        "rwandais",
        "rwandaise",
        "kigali",
        "naanga",
        "nangaa",
        "kanambe",
        "afc/m23",
        "rebelle",
        "rebelles",
        "gouvernement",
        "président",
        "president",
        "élection",
        "election",
        "politique",
        "parlement",
        "assemblée nationale",
        "assemblee nationale",
        "coup d'état",
        "coup d'etat",
    ];

    public static IQueryable<Discussion> WhereAllowed(IQueryable<Discussion> query)
    {
        query = query.Where(d => d.TopicSlug == null || d.TopicSlug != "politique");
        foreach (var term in Terms)
        {
            var needle = term;
            query = query.Where(d =>
                !((d.Title ?? "")
                    + " "
                    + (d.Body ?? "")
                    + " "
                    + (d.SourceRawBody ?? "")
                    + " "
                    + (d.SourceOriginalAuthor ?? "")
                    + " "
                    + (d.SourceExternalUrl ?? ""))
                    .ToLower()
                    .Contains(needle));
        }

        return query;
    }
}
