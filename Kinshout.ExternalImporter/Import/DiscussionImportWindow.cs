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
        // Always use the configured lookback. Recent empty runs must not shrink the window to minutes
        // (skipExisting already prevents duplicates). lastRunByProvider remains useful for monitoring only.
        _ = provider;
        _ = lastRunByProvider;
        return nowUtc.AddDays(-Math.Max(1, maxDiscussionAgeDays));
    }
}
