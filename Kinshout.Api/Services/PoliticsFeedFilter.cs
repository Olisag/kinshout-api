using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

/// <summary>
/// Keeps party politics, armed conflict, and Congo-Brazzaville content out of public feeds.
/// The same names are blocked at import in KinshasaSocialPostFilter.
/// The decision is stored on the row. Public queries read that flag
/// instead of scanning every title and body on each request.
/// </summary>
public static class PoliticsFeedFilter
{
    /// <summary>Bump when <see cref="Terms"/> or Brazzaville rules change so existing rows are reviewed again.</summary>
    public const int RuleVersion = 2;

    private static readonly string[] Terms =
    [
        // Parties / figures
        "fayulu",
        "kabund",
        "mukebayi",
        "kamerhe",
        "katumbi",
        "tshisekedi",
        "fatshi",
        "kabila",
        "bemba",
        "mlc",
        "fcc ",
        "udps",
        "topcongo",
        "top congo",
        "naanga",
        "nangaa",
        "kanambe",
        // Armed groups / conflict — including FRN
        " frn",
        "frn ",
        "#frn",
        "(frn",
        "frn/",
        "frn)",
        "front pour la refondation",
        "refondation de la nation",
        "m23",
        "afc/m23",
        "afc-m23",
        "afc m23",
        "fardc",
        "fdlr",
        "codeco",
        " adf",
        "adf ",
        "#adf",
        "rebelle",
        "rebelles",
        "rébellion",
        "rebellion",
        "insurgé",
        "insurge",
        "insurgés",
        "insurges",
        "milice",
        "milices",
        "groupe armé",
        "groupe arme",
        "groupes armés",
        "groupes armes",
        "guerre",
        "guerres",
        "guerre civile",
        "conflit armé",
        "conflit arme",
        "combats ",
        "bataille",
        "attaque armée",
        "attaque armee",
        "rwanda",
        "rwandais",
        "rwandaise",
        "kigali",
        // Institutional politics
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
        // Congo-Brazzaville (not Kinshasa / DRC)
        "brazzaville",
        "pointe-noire",
        "pointe noire",
        "congo-brazzaville",
        "congo brazzaville",
        "congo-brazza",
        "congo brazza",
        "république du congo",
        "republique du congo",
        "republic of the congo",
        "republic of congo",
        "#congobrazzaville",
        "#brazzaville",
    ];

    public static IQueryable<Discussion> WhereAllowed(IQueryable<Discussion> query) =>
        query.Where(d =>
            !d.PublicFeedBlocked
            && (d.TopicSlug == null || d.TopicSlug != "politique"));

    public static void Mark(Discussion discussion)
    {
        discussion.PublicFeedBlocked = IsBlocked(
            discussion.TopicSlug,
            discussion.Title,
            discussion.Body,
            discussion.SourceRawBody,
            discussion.SourceOriginalAuthor,
            discussion.SourceExternalUrl);
        discussion.PublicFeedRuleVersion = RuleVersion;
    }

    public static bool IsBlocked(
        string? topicSlug,
        string? title,
        string? body,
        string? rawBody,
        string? author,
        string? url)
    {
        if (string.Equals(topicSlug, "politique", StringComparison.OrdinalIgnoreCase))
            return true;

        var haystack = $"{title} {body} {rawBody} {author} {url}".ToLowerInvariant();
        foreach (var term in Terms)
        {
            if (haystack.Contains(term, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reviews rows still marked with an older rule edition. Runs after the site is
    /// already accepting requests, in small batches, so a feed query never waits on it.
    /// </summary>
    public static async Task ApplyStoredFlagsAsync(
        KinshoutDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        var reviewed = 0;
        var blocked = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Discussions
                .Where(d => d.PublicFeedRuleVersion < RuleVersion)
                .OrderBy(d => d.Id)
                .Take(25)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            foreach (var discussion in batch)
            {
                Mark(discussion);
                if (discussion.PublicFeedBlocked)
                    blocked++;
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            reviewed += batch.Count;
        }

        if (reviewed > 0)
        {
            logger.LogInformation(
                "Reviewed {Reviewed} discussions for the public feed. {Blocked} are hidden.",
                reviewed,
                blocked);
        }
    }
}
