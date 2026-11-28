namespace Kinshout.Api.Services.WhatsApp;

public sealed record TrafficObservation(
    string RouteId,
    string RoadName,
    string Segment,
    int? CurrentSeconds,
    int? ReferenceSeconds,
    string? ProviderLevel,
    bool ClosureConfirmed,
    DateTime ObservedAtUtc,
    string? Via);

public enum TrafficClass
{
    Fluid,
    Slow,
    Busy,
    Heavy,
    Closed,
    Unavailable,
}

public static class TrafficBulletinComposer
{
    public const string Closing = "Courage à tous ceux qui sont sur la route ! 💪🏾";

    public static string Store(IReadOnlyList<TrafficObservation> observations) =>
        System.Text.Json.JsonSerializer.Serialize(observations);

    public static IReadOnlyList<TrafficObservation> Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return System.Text.Json.JsonSerializer.Deserialize<List<TrafficObservation>>(json) ?? [];
    }

    public static int WholeMinutes(int seconds) =>
        (int)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero);

    public static TrafficClass Classify(TrafficObservation observation)
    {
        if (observation.ClosureConfirmed)
            return TrafficClass.Closed;
        if (observation.CurrentSeconds is null)
            return TrafficClass.Unavailable;
        if (!string.IsNullOrWhiteSpace(observation.ProviderLevel))
        {
            return observation.ProviderLevel.Trim().ToLowerInvariant() switch
            {
                "heavy" => TrafficClass.Heavy,
                "moderate" or "medium" => TrafficClass.Busy,
                "light" or "low" => TrafficClass.Fluid,
                _ => FromRatio(observation),
            };
        }
        return FromRatio(observation);
    }

    public static TrafficClass FromRatio(TrafficObservation observation)
    {
        if (observation.CurrentSeconds is null || observation.ReferenceSeconds is null || observation.ReferenceSeconds <= 0)
            return observation.CurrentSeconds is null ? TrafficClass.Unavailable : TrafficClass.Fluid;
        var ratio = observation.CurrentSeconds.Value / (double)observation.ReferenceSeconds.Value;
        if (ratio < 1.20)
            return TrafficClass.Fluid;
        if (ratio < 1.50)
            return TrafficClass.Slow;
        if (ratio < 2.00)
            return TrafficClass.Busy;
        return TrafficClass.Heavy;
    }

    public static string? Format(IReadOnlyList<TrafficObservation> observations, DateTime localNow, int maxAgeMinutes, int minSegments, int maxSegments, out string skipReason)
    {
        var fresh = observations
            .Where(o => AgeMinutes(o, localNow) <= maxAgeMinutes)
            .Where(o => Classify(o) != TrafficClass.Unavailable)
            .Where(o => o.CurrentSeconds is not null || o.ClosureConfirmed)
            .ToList();
        if (fresh.Count < minSegments)
        {
            skipReason = "not-enough-fresh-segments";
            return null;
        }
        var ranked = fresh
            .OrderBy(o => Rank(Classify(o)))
            .ThenByDescending(DisplayedDelay)
            .Take(maxSegments)
            .ToList();
        var stamps = ranked.Select(o => o.ObservedAtUtc).ToList();
        var oldest = stamps.Min();
        var newest = stamps.Max();
        var observed = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(newest, DateTimeKind.Utc), FeedImportClock.Kinshasa());
        var lines = new List<string>
        {
            "🚦 KIN TRAFIC | by Kinoiserie",
            "",
            $"📅 {WeatherBulletinComposer.FrenchDate(observed)}",
            (newest - oldest).TotalMinutes > 10
                ? $"🕒 Situation entre {Clock(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(oldest, DateTimeKind.Utc), FeedImportClock.Kinshasa()))} et {Clock(observed)}"
                : $"🕒 Situation à {Clock(observed)}",
            "",
        };
        for (var i = 0; i < ranked.Count; i++)
        {
            if (i > 0)
                lines.Add("");
            lines.AddRange(Block(ranked[i]));
        }
        lines.Add("");
        lines.Add(Closing);
        lines.Add("");
        lines.Add("🌐 https://kinoiserie.com");
        skipReason = "";
        return string.Join("\n", lines);
    }

    public static bool QualifiesForMidday(IReadOnlyList<TrafficObservation> current, IReadOnlyList<TrafficObservation>? previous, int minHeavy)
    {
        var heavy = current.Count(o => Classify(o) == TrafficClass.Heavy);
        if (heavy >= minHeavy)
            return true;
        if (current.Any(o => o.ClosureConfirmed))
            return true;
        if (previous is null)
            return false;
        foreach (var item in current)
        {
            var before = previous.FirstOrDefault(p => p.RouteId == item.RouteId);
            if (before?.CurrentSeconds is null || item.CurrentSeconds is null)
                continue;
            var delta = WholeMinutes(item.CurrentSeconds.Value) - WholeMinutes(before.CurrentSeconds.Value);
            if (Math.Abs(delta) >= 10)
                return true;
        }
        return false;
    }

    private static double AgeMinutes(TrafficObservation observation, DateTime localNow)
    {
        var utc = localNow.Kind == DateTimeKind.Utc
            ? localNow
            : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified), FeedImportClock.Kinshasa());
        return (utc - DateTime.SpecifyKind(observation.ObservedAtUtc, DateTimeKind.Utc)).TotalMinutes;
    }

    private static int Rank(TrafficClass kind) => kind switch
    {
        TrafficClass.Closed => 0,
        TrafficClass.Heavy => 1,
        TrafficClass.Busy => 2,
        TrafficClass.Slow => 3,
        TrafficClass.Fluid => 4,
        _ => 5,
    };

    private static int DisplayedDelay(TrafficObservation observation)
    {
        if (observation.CurrentSeconds is null || observation.ReferenceSeconds is null)
            return 0;
        var delay = WholeMinutes(observation.CurrentSeconds.Value) - WholeMinutes(observation.ReferenceSeconds.Value);
        return delay > 0 ? delay : 0;
    }

    private static IEnumerable<string> Block(TrafficObservation observation)
    {
        var kind = Classify(observation);
        var emoji = kind switch
        {
            TrafficClass.Heavy => "🔴",
            TrafficClass.Busy => "🟠",
            TrafficClass.Slow => "🟡",
            TrafficClass.Closed => "⛔",
            TrafficClass.Unavailable => "⚪",
            _ => "🟢",
        };
        yield return $"{emoji} *{observation.RoadName}*";
        if (!string.IsNullOrWhiteSpace(observation.Segment))
            yield return observation.Segment;
        if (kind == TrafficClass.Closed)
        {
            yield return "Route fermée.";
            yield break;
        }
        var delay = DisplayedDelay(observation);
        if (observation.CurrentSeconds is not null && observation.ReferenceSeconds is not null && delay > 0)
        {
            yield return $"⏱️ {WholeMinutes(observation.CurrentSeconds.Value)} min au lieu de {WholeMinutes(observation.ReferenceSeconds.Value)} min (+{delay} min)";
            yield break;
        }
        if (observation.CurrentSeconds is not null && kind != TrafficClass.Fluid)
        {
            yield return $"⏱️ {WholeMinutes(observation.CurrentSeconds.Value)} min";
            yield break;
        }
        yield return "Circulation fluide.";
    }

    private static string Clock(DateTime local) => $"{local.Hour:00}h{local.Minute:00}";
}
