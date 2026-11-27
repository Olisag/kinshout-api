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
        Insolite,
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

    public static DateTime KinshasaLocal(DateTime utcNow) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), KinshasaZone());

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

    /// <summary>The actor rejects a query of more than 100 characters, so the packer stops at 99.</summary>
    internal const int FacebookQueryLimit = 99;

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

    /// <summary>
    /// 08:00, 14:00 and 20:00 Kinshasa share five viral searches and three insolite searches.
    /// Insolite is first so a full keep-cap cannot skip it.
    /// </summary>
    public static IReadOnlyList<DiscoveryQuery> TikTokSearches(DateTime utcNow)
    {
        var slot = DaySlot(utcNow, 11, 17);
        var day = KinshasaDate(utcNow).DayNumber;
        var viralCount = slot == 2 ? 1 : 2;
        var queries = new List<DiscoveryQuery>
        {
            new(Quote(Insolite[(day + slot) % Insolite.Length]), Lane.Insolite, SearchEstablishesRelevance: true),
        };
        for (var index = 0; index < viralCount; index++)
        {
            queries.Add(new DiscoveryQuery(
                Quote(Buzz[(day + slot + index) % Buzz.Length]),
                Lane.Buzz,
                SearchEstablishesRelevance: true));
        }

        return queries;
    }

    /// <summary>Four verified profiles per run, walking the list so none of them is skipped forever.</summary>
    public static IReadOnlyList<string> RotateHandles(
        IReadOnlyList<string> handles,
        DateTime utcNow,
        int perRun,
        IReadOnlyDictionary<string, int>? kept = null)
    {
        if (handles.Count == 0 || perRun < 1)
            return [];

        var ordered = handles
            .Select(handle => handle.Trim().TrimStart('@'))
            .Where(handle => handle.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(handle => kept is not null && kept.TryGetValue(handle, out var score) ? score : 0)
            .ThenBy(handle => handle, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count == 0)
            return [];

        var start = (int)((KinshasaDate(utcNow).DayNumber * 4L + DaySlot(utcNow, 11, 17)) % ordered.Count);
        var chosen = new List<string>();
        var take = Math.Min(perRun, ordered.Count);
        for (var index = 0; index < take; index++)
            chosen.Add(ordered[(start + index) % ordered.Count]);
        return chosen;
    }

    /// <summary>
    /// One X search. Catalogue names are not added: a bare newsroom name was returning that
    /// account's whole timeline. Verified handles are folded into the same search.
    /// </summary>
    public static string XQuery(string existingQuery, IReadOnlyList<string> handles)
    {
        var from = handles
            .Select(handle => handle.Trim().TrimStart('@'))
            .Where(handle => handle.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(handle => "from:" + handle)
            .ToList();
        if (from.Count == 0)
            return existingQuery;

        var addition = string.Join(" OR ", from);
        var lang = existingQuery.IndexOf(" lang:", StringComparison.OrdinalIgnoreCase);
        if (lang > 0 && existingQuery[lang - 1] == ')')
            return existingQuery[..(lang - 1)] + " OR " + addition + existingQuery[(lang - 1)..];
        if (lang >= 0)
            return "(" + existingQuery[..lang].Trim() + " OR " + addition + ")" + existingQuery[lang..];
        return existingQuery + " OR " + addition;
    }

    /// <summary>
    /// One short Kinshasa search a day. Four kept posts use the weekly 16
    /// through Thursday instead of finishing it on Tuesday.
    /// </summary>
    public static IReadOnlyList<DiscoveryQuery> FacebookSearches(
        DateTime utcNow,
        IReadOnlyList<string> verifiedUsernames)
    {
        var day = KinshasaDate(utcNow).DayNumber;
        var lanes = new[]
        {
            LaneQuery(Lane.Theme, "\"concert kinshasa\" OR \"agenda kinshasa\""),
            new DiscoveryQuery(Quote(Buzz[day % Buzz.Length]), Lane.Buzz, SearchEstablishesRelevance: true),
            new DiscoveryQuery(Quote(Insolite[day % Insolite.Length]), Lane.Insolite, SearchEstablishesRelevance: true),
            LaneQuery(Lane.Theme, "\"quartier kinshasa\" OR \"transport kinshasa\""),
            CatalogueUsernames(verifiedUsernames),
        };
        return [lanes[day % lanes.Length]];
    }

    public const int FacebookResultsPerSearch = 8;

    private static DiscoveryQuery LaneQuery(Lane lane, string text) =>
        new(text, lane, SearchEstablishesRelevance: true);

    private static DiscoveryQuery CatalogueUsernames(IReadOnlyList<string> usernames)
    {
        var names = usernames
            .Select(name => name.Trim().TrimStart('@'))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
            return LaneQuery(Lane.Catalogue, "kinshasa");

        var query = string.Join(" OR ", names);
        if (query.Length > FacebookQueryLimit)
        {
            var cut = query.LastIndexOf(" OR ", FacebookQueryLimit, StringComparison.Ordinal);
            query = (cut > 0 ? query[..cut] : query[..FacebookQueryLimit]).Trim();
        }
        return new DiscoveryQuery(query, Lane.Catalogue, SearchEstablishesRelevance: true);
    }

    private static int DaySlot(DateTime utcNow, int secondHour, int thirdHour)
    {
        var hour = KinshasaLocal(utcNow).Hour;
        if (hour < secondHour)
            return 0;
        return hour < thirdHour ? 1 : 2;
    }
}
