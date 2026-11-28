using Kinshout.Api.Configuration;

namespace Kinshout.Api.Services.WhatsApp;

public sealed record WhatsAppSlot(
    string Id,
    string Category,
    string Purpose,
    TimeOnly LocalTime,
    int GraceMinutes,
    DayOfWeek[]? Days,
    string? PairId);

public static class WhatsAppSchedule
{
    private static readonly DayOfWeek[] Weekdays =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
    ];

    public static IReadOnlyList<WhatsAppSlot> All { get; } =
    [
        new("weather-retrieve", "meteo", "retrieve", new TimeOnly(5, 20), 20, null, "weather"),
        new("weather-publish", "meteo", "publish", new TimeOnly(5, 30), 210, null, "weather"),
        new("weather-refresh", "meteo-refresh", "publish", new TimeOnly(12, 0), 30, null, "weather-refresh"),
        new("traffic-morning-retrieve", "trafic", "retrieve", new TimeOnly(6, 20), 15, null, "traffic-morning"),
        new("traffic-morning-publish", "trafic", "publish", new TimeOnly(6, 30), 15, null, "traffic-morning"),
        new("traffic-morning-update-retrieve", "trafic", "retrieve", new TimeOnly(8, 20), 15, null, "traffic-morning-update"),
        new("traffic-morning-update-publish", "trafic", "publish", new TimeOnly(8, 30), 15, null, "traffic-morning-update"),
        new("editorial-essentiel", "essentiel", "publish", new TimeOnly(9, 30), 60, null, null),
        new("editorial-buzz", "buzz", "publish", new TimeOnly(11, 30), 60, null, null),
        new("traffic-midday-retrieve", "trafic", "retrieve", new TimeOnly(12, 20), 15, null, "traffic-midday"),
        new("traffic-midday-publish", "trafic", "publish", new TimeOnly(12, 30), 15, null, "traffic-midday"),
        new("editorial-insolite", "insolite", "publish", new TimeOnly(14, 0), 60, null, null),
        new("traffic-afternoon-retrieve", "trafic", "retrieve", new TimeOnly(15, 20), 15, null, "traffic-afternoon"),
        new("traffic-afternoon-publish", "trafic", "publish", new TimeOnly(15, 30), 15, null, "traffic-afternoon"),
        new("events-thursday", "evenements", "publish", new TimeOnly(16, 30), 120, [DayOfWeek.Thursday], null),
        new("traffic-evening-retrieve", "trafic", "retrieve", new TimeOnly(17, 20), 15, null, "traffic-evening"),
        new("traffic-evening-publish", "trafic", "publish", new TimeOnly(17, 30), 15, null, "traffic-evening"),
        new("editorial-musique", "musique", "publish", new TimeOnly(18, 30), 60, Weekdays, null),
        new("editorial-highlights", "highlights", "publish", new TimeOnly(18, 30), 60, [DayOfWeek.Sunday], null),
        new("interaction", "interaction", "publish", new TimeOnly(20, 30), 60, null, null),
        new("editorial-fierte", "fierte", "publish", new TimeOnly(21, 30), 60, null, null),
        new("events-friday", "evenements", "publish", new TimeOnly(13, 0), 120, [DayOfWeek.Friday], null),
        new("events-saturday", "evenements", "publish", new TimeOnly(10, 30), 120, [DayOfWeek.Saturday], null),
    ];

    public static IReadOnlyList<WhatsAppSlot> Open(DateTime utc, WhatsAppPublishingSettings settings)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), FeedImportClock.Kinshasa());
        var open = new List<WhatsAppSlot>();
        foreach (var slot in All)
        {
            if (!IsEnabled(slot, settings))
                continue;
            if (slot.Days is not null && !slot.Days.Contains(local.DayOfWeek))
                continue;
            var scheduled = local.Date.Add(slot.LocalTime.ToTimeSpan());
            if (local < scheduled || local >= scheduled.AddMinutes(slot.GraceMinutes))
                continue;
            open.Add(slot);
        }
        return open;
    }

    public static bool IsEnabled(WhatsAppSlot slot, WhatsAppPublishingSettings settings)
    {
        if (slot.Id == "weather-refresh")
            return settings.WeatherEnabled && settings.WeatherForecastRefreshEnabled;
        if (slot.Id.StartsWith("traffic-midday", StringComparison.Ordinal))
            return settings.TrafficEnabled && settings.MiddayTrafficEnabled;
        if (slot.Category is "meteo" or "meteo-refresh")
            return settings.WeatherEnabled;
        if (slot.Category == "trafic")
            return settings.TrafficEnabled;
        if (slot.Category == "interaction")
            return settings.InteractionEnabled;
        if (slot.Category == "evenements")
            return settings.EventsEnabled && settings.EditorialEnabled;
        return settings.EditorialEnabled;
    }

    public static string Key(string channelId, WhatsAppSlot slot, DateTime localDate) =>
        $"{channelId}|{slot.Id}|{localDate:yyyy-MM-dd}|{slot.LocalTime:HH:mm}";

    public static DateTime ScheduledUtc(DateTime utc, WhatsAppSlot slot)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), FeedImportClock.Kinshasa());
        var scheduled = local.Date.Add(slot.LocalTime.ToTimeSpan());
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(scheduled, DateTimeKind.Unspecified), FeedImportClock.Kinshasa());
    }
}
