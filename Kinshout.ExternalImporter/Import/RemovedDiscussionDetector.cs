using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Import;

internal static class RemovedDiscussionDetector
{
    private const double MinimumSeenRatio = 0.25;
    private const int MinimumKnownForRatioCheck = 10;

    public static bool ShouldDetectRemovals(int seenCount, int knownCount) =>
        seenCount > 0
        && knownCount > 0
        && (knownCount <= MinimumKnownForRatioCheck || seenCount >= knownCount * MinimumSeenRatio);

    public static IReadOnlyList<ImportExternalDiscussionDto> BuildRemovals(
        ExternalProviderSettings provider,
        IReadOnlySet<string> seenExternalIds,
        IReadOnlySet<string> knownKeys,
        DateTime now)
    {
        var knownForProvider = ImportDiscussionKeys.KnownExternalIdsForProvider(knownKeys, provider.Provider).ToList();
        if (knownForProvider.Count == 0)
            return [];

        if (!ShouldDetectRemovals(seenExternalIds.Count, knownForProvider.Count))
            return [];

        return knownForProvider
            .Where(id => !seenExternalIds.Contains(id))
            .Select(id => DiscussionMapper.ToRemovalDto(provider, id, now))
            .ToList();
    }
}
