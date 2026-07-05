using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers;

namespace Kinshout.ExternalImporter.Import;

public static class DiscussionMapper
{
    public static ImportExternalDiscussionDto? ToImportDto(SourceFeedDiscussion feed, ExternalProviderSettings provider, DateTime now)
    {
        var externalUrl = Clean(feed.ExternalUrl);
        var title = Clean(feed.Title);
        var body = Clean(feed.Body);
        if (string.IsNullOrWhiteSpace(externalUrl) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            return null;

        var externalId = Clean(feed.ExternalId) ?? StableId(externalUrl);

        return new ImportExternalDiscussionDto(
            Source: new ImportExternalDiscussionSourceDto(
                Provider: provider.Provider,
                ProviderName: provider.ProviderName,
                ExternalId: externalId,
                ExternalUrl: externalUrl,
                ImportedAt: now,
                LastSeenAt: now,
                FirstSeenAt: null),
            Title: title,
            Body: body,
            OriginalAuthor: Clean(feed.OriginalAuthor),
            EngagementScore: feed.EngagementScore,
            Status: Clean(feed.Status) ?? "active",
            PublishedAt: feed.PublishedAt);
    }

    public static ImportExternalDiscussionDto ToRemovalDto(
        ExternalProviderSettings provider,
        string externalId,
        DateTime now) =>
        new(
            Source: new ImportExternalDiscussionSourceDto(
                Provider: provider.Provider,
                ProviderName: provider.ProviderName,
                ExternalId: externalId.Trim(),
                ExternalUrl: $"https://removed.local/{provider.Provider}/{Uri.EscapeDataString(externalId.Trim())}",
                ImportedAt: now,
                LastSeenAt: now,
                FirstSeenAt: null),
            Title: "removed",
            Body: "removed",
            OriginalAuthor: null,
            EngagementScore: null,
            Status: "removed",
            PublishedAt: null);

    private static string StableId(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
