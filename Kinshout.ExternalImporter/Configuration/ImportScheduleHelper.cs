namespace Kinshout.ExternalImporter.Configuration;

internal static class ImportScheduleHelper
{
    public static TimeSpan DelayUntilNextRun(ImportScheduleSettings schedule, DateTime utcNow)
    {
        if (schedule.RunAtHour is not { } hour)
            return TimeSpan.FromHours(Math.Max(1, schedule.IntervalHours));

        var timeZone = ResolveTimeZone(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var nextLocal = localNow.Date.AddHours(Math.Clamp(hour, 0, 23));
        if (localNow >= nextLocal)
            nextLocal = nextLocal.AddDays(1);

        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal, timeZone);
        var delay = nextUtc - utcNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public static string DescribeNextRun(ImportScheduleSettings schedule, DateTime utcNow)
    {
        if (schedule.RunAtHour is not { } hour)
            return $"every {Math.Max(1, schedule.IntervalHours)} hour(s)";

        var timeZone = ResolveTimeZone(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var nextLocal = localNow.Date.AddHours(Math.Clamp(hour, 0, 23));
        if (localNow >= nextLocal)
            nextLocal = nextLocal.AddDays(1);

        return $"{nextLocal:yyyy-MM-dd HH:mm} {schedule.TimeZoneId} ({hour:00}:00 daily)";
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            Console.WriteLine($"Unknown timezone '{timeZoneId}', falling back to UTC+1 (Kinshasa).");
            return TimeZoneInfo.CreateCustomTimeZone("Africa/Kinshasa", TimeSpan.FromHours(1), "Kinshasa", "Kinshasa");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Africa/Kinshasa", TimeSpan.FromHours(1), "Kinshasa", "Kinshasa");
        }
    }
}
