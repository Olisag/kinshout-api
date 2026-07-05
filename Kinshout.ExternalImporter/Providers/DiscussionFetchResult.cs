namespace Kinshout.ExternalImporter.Providers;

public sealed class DiscussionFetchResult
{
    public IReadOnlyList<SourceFeedDiscussion> Discussions { get; init; } = [];
    public IReadOnlySet<string> SeenExternalIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static DiscussionFetchResult From(
        IReadOnlyList<SourceFeedDiscussion> discussions,
        IReadOnlySet<string> seenExternalIds) =>
        new()
        {
            Discussions = discussions,
            SeenExternalIds = seenExternalIds,
        };
}
