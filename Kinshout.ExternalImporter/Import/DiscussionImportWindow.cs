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
            var since = lastRun.ToUniversalTime();
            return since < floor ? floor : since;
        }

        return floor;
    }
}
