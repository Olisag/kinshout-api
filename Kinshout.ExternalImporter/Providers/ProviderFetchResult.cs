namespace Kinshout.ExternalImporter.Providers;

public sealed record ProviderFetchResult(
    IReadOnlyList<SourceFeedAdvert> Adverts,
    IReadOnlySet<string> SeenExternalIds)
{
    public static ProviderFetchResult From(
        IReadOnlyList<SourceFeedAdvert> adverts,
        IReadOnlySet<string>? seenExternalIds = null)
    {
        seenExternalIds ??= adverts
            .Select(a => a.ExternalId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ProviderFetchResult(adverts, seenExternalIds);
    }
}
