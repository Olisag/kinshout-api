using Kinshout.ExternalImporter.Configuration;

namespace Kinshout.ExternalImporter.Import;

internal static class RemovedListingDetector
{
    private const double MinimumSeenRatio = 0.25;
    private const int MinimumKnownForRatioCheck = 10;

    public static bool ShouldDetectRemovals(int seenCount, int knownCount) =>
        seenCount > 0
        && knownCount > 0
        && (knownCount <= MinimumKnownForRatioCheck || seenCount >= knownCount * MinimumSeenRatio);

    public static IReadOnlyList<ImportExternalAdvertDto> BuildRemovals(
        ExternalProviderSettings provider,
        IReadOnlySet<string> seenExternalIds,
        IReadOnlySet<string> knownKeys,
        DateTime now)
    {
        var knownForProvider = ImportAdvertKeys.KnownExternalIdsForProvider(knownKeys, provider.Provider).ToList();
        if (knownForProvider.Count == 0)
            return [];

        if (!ShouldDetectRemovals(seenExternalIds.Count, knownForProvider.Count))
            return [];

        return knownForProvider
            .Where(id => !seenExternalIds.Contains(id))
            .Select(id => AdvertMapper.ToRemovalDto(provider, id, now))
            .ToList();
    }
}
