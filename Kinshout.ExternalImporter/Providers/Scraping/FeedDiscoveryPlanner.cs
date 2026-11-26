using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Providers.Scraping;

/// <summary>
/// Replaces the fixed search lists. Each platform spends its existing number of
/// paid searches on open buzz, the day's theme, and a rotating slice of the
/// people listed for that platform. The next run continues through the list.
/// </summary>
internal static class FeedDiscoveryPlanner
{
    internal enum Lane
    {
        Buzz,
        Theme,
        Catalogue,
    }

    internal sealed record DiscoveryQuery(string Text, Lane Lane, bool SearchEstablishesRelevance);

    private static readonly string[] Buzz =
    [
        "buzz congo",
        "viral rdc",
        "bana kin",
        "diaspora congolaise",
        "ça chauffe kinshasa",
        "fait le buzz congo",
        "makambo kinshasa",
        "tendance congolaise",
    ];

    private static readonly string[] Insolite =
    [
        "insolite kinshasa",
        "du jamais vu congo",
        "biloko ya kokamwa",
        "incroyable mais vrai kinshasa",
        "talent insolite congo",
        "somo kinshasa",
    ];

    public static DateOnly KinshasaDate(DateTime utcNow)
    {
        var zone = KinshasaZone();
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone));
    }

    public static IReadOnlyList<DiscoveryQuery> SelectForPlatform(
        string platform,
        DateTime utcNow,
        int slots,
        IReadOnlyList<CatalogueSubject> subjects)
    {
        if (slots <= 0)
            return [];

        var date = KinshasaDate(utcNow);
        var lanes = LanesFor(slots);
        var groupSize = GroupSize(platform);
        var catalogueSlots = lanes.Count(lane => lane == Lane.Catalogue);
        var packedCatalogue = platform.Equals("facebook", StringComparison.OrdinalIgnoreCase)
            ? PackQueries(subjects, FacebookQueryLimit)
            : null;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queries = new List<DiscoveryQuery>(slots);
        var buzzCursor = 0;
        var catalogueSlot = 0;

        foreach (var lane in lanes)
        {
            if (lane == Lane.Catalogue)
            {
                if (packedCatalogue is not null)
                {
                    catalogueSlot++;
                    if (packedCatalogue.Count == 0)
                    {
                        queries.Add(BuzzQuery(date, buzzCursor++));
                        continue;
                    }

                    var index = (int)(((long)date.DayNumber * Math.Max(1, catalogueSlots) + (catalogueSlot - 1)) % packedCatalogue.Count);
                    queries.Add(new DiscoveryQuery(packedCatalogue[index], Lane.Catalogue, SearchEstablishesRelevance: true));
                    continue;
                }

                var group = TakeGroup(subjects, date.DayNumber, catalogueSlot, catalogueSlots, groupSize, used);
                catalogueSlot++;
                if (group.Count == 0)
                {
                    queries.Add(BuzzQuery(date, buzzCursor++));
                    continue;
                }

                queries.Add(new DiscoveryQuery(Join(group), Lane.Catalogue, SearchEstablishesRelevance: true));
                continue;
            }

            if (lane == Lane.Theme)
            {
                queries.Add(new DiscoveryQuery(Theme(date.DayOfWeek), Lane.Theme, SearchEstablishesRelevance: true));
                continue;
            }

            queries.Add(BuzzQuery(date, buzzCursor++));
        }

        return queries;
    }

    /// <summary>
    /// One X request. The political block stays. The old keyword gate is replaced
    /// by today's catalogue names plus buzz, insolite and the daily theme.
    /// </summary>
    public static string BuildXQuery(string existingQuery, DateTime utcNow, IReadOnlyList<CatalogueSubject> subjects)
    {
        var date = KinshasaDate(utcNow);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var window = TakeGroup(subjects, date.DayNumber, catalogueSlot: 0, catalogueSlots: 1, groupSize: 10, used);
        var parts = window.Select(subject => Parenthesize(subject.Query)).ToList();
        parts.Add(Quote(Buzz[date.DayNumber % Buzz.Length]));
        parts.Add(Quote(Insolite[date.DayNumber % Insolite.Length]));
        parts.Add(Quote(Theme(date.DayOfWeek)));
        var addition = "(" + string.Join(" OR ", parts) + ")";

        var lang = existingQuery.IndexOf(" lang:", StringComparison.OrdinalIgnoreCase);
        var suffix = lang >= 0
            ? existingQuery[lang..]
            : " lang:fr min_faves:5 -politique -élection";
        return addition + suffix;
    }

    internal const int FacebookQueryLimit = 100;

    internal static int GroupSize(string platform) =>
        platform.Equals("facebook", StringComparison.OrdinalIgnoreCase) ? 15 : 1;

    /// <summary>
    /// Facebook's post search rejects a query longer than 100 characters.
    /// Names stay in order and each name is in one group, so the daily rotation does not skip anyone.
    /// </summary>
    internal static IReadOnlyList<string> PackQueries(IReadOnlyList<CatalogueSubject> subjects, int limit)
    {
        var packed = new List<string>();
        var batch = new List<CatalogueSubject>();
        foreach (var subject in subjects)
        {
            if (string.IsNullOrWhiteSpace(subject.Query))
                continue;

            var alone = Join([subject]);
            if (alone.Length > limit)
                continue;

            if (batch.Count == 0)
            {
                batch.Add(subject);
                continue;
            }

            var candidate = Join(batch.Append(subject).ToList());
            if (candidate.Length <= limit)
            {
                batch.Add(subject);
                continue;
            }

            packed.Add(Join(batch));
            batch = [subject];
        }

        if (batch.Count > 0)
            packed.Add(Join(batch));
        return packed;
    }

    internal static IReadOnlyList<Lane> LanesFor(int slots)
    {
        if (slots <= 0)
            return [];

        var lanes = new List<Lane> { Lane.Buzz };
        if (slots >= 3)
            lanes.Add(Lane.Theme);
        while (lanes.Count < slots)
            lanes.Add(Lane.Catalogue);
        return lanes;
    }

    internal static string Theme(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "weekend kinshasa",
        DayOfWeek.Tuesday => "humour congolais",
        DayOfWeek.Wednesday => "congolais de paris",
        DayOfWeek.Thursday => "interview congolaise",
        DayOfWeek.Friday => "musique congolaise",
        DayOfWeek.Saturday => "football congolais",
        _ => "culture congolaise",
    };

    private static DiscoveryQuery BuzzQuery(DateOnly date, int cursor)
    {
        var buzz = Buzz[(date.DayNumber + cursor) % Buzz.Length];
        var insolite = Insolite[(date.DayNumber + cursor) % Insolite.Length];
        return new DiscoveryQuery($"{Quote(buzz)} OR {Quote(insolite)}", Lane.Buzz, SearchEstablishesRelevance: true);
    }

    private static List<CatalogueSubject> TakeGroup(
        IReadOnlyList<CatalogueSubject> subjects,
        int dayNumber,
        int catalogueSlot,
        int catalogueSlots,
        int groupSize,
        HashSet<string> used)
    {
        var group = new List<CatalogueSubject>();
        if (subjects.Count == 0 || groupSize <= 0)
            return group;

        var stride = Math.Max(1, catalogueSlots) * groupSize;
        var start = (int)(((long)dayNumber * stride + (long)catalogueSlot * groupSize) % subjects.Count);
        for (var offset = 0; offset < subjects.Count && group.Count < groupSize; offset++)
        {
            var subject = subjects[(start + offset) % subjects.Count];
            if (!used.Add(subject.Name))
                continue;
            group.Add(subject);
        }

        return group;
    }

    private static string Join(IReadOnlyList<CatalogueSubject> group) =>
        group.Count == 1 ? group[0].Query : string.Join(" OR ", group.Select(subject => Parenthesize(subject.Query)));

    private static string Parenthesize(string query) =>
        query.Contains(' ') || query.Contains("OR", StringComparison.OrdinalIgnoreCase) ? $"({query})" : query;

    private static string Quote(string text) =>
        text.Contains(' ') ? $"\"{text}\"" : text;

    private static TimeZoneInfo KinshasaZone()
    {
        foreach (var id in new[] { "Africa/Kinshasa", "W. Central Africa Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Africa/Kinshasa", TimeSpan.FromHours(1), "Africa/Kinshasa", "Africa/Kinshasa");
    }
}
