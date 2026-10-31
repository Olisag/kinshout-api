namespace Kinshout.ExternalImporter.Import;

internal static class DiscussionImportWindow
{
    public const string ImportKind = "discussion";

    public static DateTime ResolveSinceUtc(
        string provider,
        IReadOnlyDictionary<string, DateTime> lastRunByProvider,
        DateTime nowUtc,
        int maxDiscussionAgeDays)
    {
        var floor = nowUtc.AddDays(-Math.Max(1, maxDiscussionAgeDays));
        if (lastRunByProvider.TryGetValue(provider, out var lastRun))
        {
            // API returns UTC timestamps; treat Unspecified as UTC so local offset does not push "since" into the future.
            var since = lastRun.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(lastRun, DateTimeKind.Utc)
                : lastRun.ToUniversalTime();
            return since < floor ? floor : since;
        }

        return floor;
    }
}
