namespace Kinshout.Api.Services;

public readonly record struct DueFeedImport(string SlotKey, string WorkflowFile);

/// <summary>
/// Kinshasa timetable for the feed importers. A slot stays due for the catch-up
/// window and then expires, so a missed morning does not run on top of the next one.
/// </summary>
public static class FeedImportClock
{
    private readonly record struct Slot(string Platform, string WorkflowFile, TimeOnly LocalTime);

    private static readonly Slot[] Slots =
    [
        new("tiktok", "external-discussion-importer-prod-tiktok-daily.yml", new TimeOnly(8, 0)),
        new("tiktok", "external-discussion-importer-prod-tiktok-daily.yml", new TimeOnly(14, 0)),
        new("tiktok", "external-discussion-importer-prod-tiktok-daily.yml", new TimeOnly(20, 0)),
        new("x", "external-discussion-importer-prod-x-daily.yml", new TimeOnly(11, 0)),
        new("x", "external-discussion-importer-prod-x-daily.yml", new TimeOnly(18, 0)),
        new("youtube-buzz", "external-discussion-importer-prod-youtube-buzz.yml", new TimeOnly(9, 0)),
        new("youtube-buzz", "external-discussion-importer-prod-youtube-buzz.yml", new TimeOnly(19, 0)),
        new("youtube-channels", "external-discussion-importer-prod-youtube.yml", new TimeOnly(0, 0)),
        new("youtube-channels", "external-discussion-importer-prod-youtube.yml", new TimeOnly(6, 0)),
        new("youtube-channels", "external-discussion-importer-prod-youtube.yml", new TimeOnly(12, 0)),
        new("youtube-channels", "external-discussion-importer-prod-youtube.yml", new TimeOnly(18, 0)),
        new("facebook", "external-discussion-importer-prod-schedule.yml", new TimeOnly(16, 0)),
    ];

    public static IReadOnlyList<DueFeedImport> Due(DateTime utc, int catchUpMinutes)
    {
        var window = TimeSpan.FromMinutes(Math.Clamp(catchUpMinutes, 15, 180));
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Kinshasa());
        var due = new List<DueFeedImport>();
        foreach (var slot in Slots)
        {
            var scheduled = local.Date.Add(slot.LocalTime.ToTimeSpan());
            if (local < scheduled || local >= scheduled.Add(window))
                continue;
            due.Add(new DueFeedImport(
                $"{slot.Platform}:{scheduled:yyyy-MM-dd}:{scheduled:HH:mm}",
                slot.WorkflowFile));
        }
        return due;
    }

    private static TimeZoneInfo Kinshasa()
    {
        foreach (var id in new[] { "Africa/Kinshasa", "W. Central Africa Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Africa/Kinshasa", TimeSpan.FromHours(1), "Kinshasa", "Kinshasa");
    }
}
