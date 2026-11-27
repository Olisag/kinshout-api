namespace Kinshout.ExternalImporter.Providers;

public sealed class DiscussionFetchResult
{
    public IReadOnlyList<SourceFeedDiscussion> Discussions { get; init; } = [];
    public IReadOnlySet<string> SeenExternalIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int ResultsRequested { get; init; }
    public IReadOnlyList<DiscoveryCheck> Checks { get; init; } = [];

    public static DiscussionFetchResult From(
        IReadOnlyList<SourceFeedDiscussion> discussions,
        IReadOnlySet<string> seenExternalIds,
        int resultsRequested = 0,
        IReadOnlyList<DiscoveryCheck>? checks = null) =>
        new()
        {
            Discussions = discussions,
            SeenExternalIds = seenExternalIds,
            ResultsRequested = resultsRequested,
            Checks = checks ?? [],
        };
}

public sealed record DiscoveryCheck(string Handle, int Kept);
