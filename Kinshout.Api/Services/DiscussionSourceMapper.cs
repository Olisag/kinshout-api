using Kinshout.Api.Dtos;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

internal static class DiscussionSourceMapper
{
    public static bool IsSameContent(Discussion existing, string rawBody, int? engagementScore) =>
        string.Equals(NormalizeRaw(existing.SourceRawBody), NormalizeRaw(rawBody), StringComparison.Ordinal)
        && existing.SourceEngagementScore == engagementScore;

    private static string NormalizeRaw(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private static string PlayableExternalUrl(Discussion discussion)
    {
        var url = discussion.SourceExternalUrl ?? string.Empty;
        if (string.Equals(discussion.SourceProvider, DiscussionSourceProvider.TikTok, StringComparison.OrdinalIgnoreCase)
            && ImportedVideoStore.IsHostedFile(discussion.VideoUrlsJson))
        {
            return string.Empty;
        }

        return url;
    }

    public static DiscussionSourceDto? ToSourceDto(Discussion discussion)
    {
        if (!discussion.IsExternal || string.IsNullOrWhiteSpace(discussion.SourceProvider))
            return null;

        return new DiscussionSourceDto(
            discussion.SourceProvider,
            discussion.SourceProviderName ?? DiscussionSourceProvider.DisplayName(discussion.SourceProvider),
            discussion.SourceExternalId ?? string.Empty,
            // The app opens a TikTok player whenever this is a TikTok link,
            // even when the clip is already a file. Leave the link in the
            // database and omit it here so clients play the file only.
            PlayableExternalUrl(discussion),
            discussion.SourceImportedAt ?? discussion.CreatedAt,
            discussion.SourceLastSeenAt ?? discussion.UpdatedAt,
            discussion.SourceFirstSeenAt ?? discussion.CreatedAt,
            discussion.SourceOriginalAuthor,
            discussion.SourceEngagementScore,
            discussion.ExternalPublishedAt,
            discussion.SourceLikeCount,
            discussion.SourceViewCount);
    }

    /// <summary>
    /// When the discussion showed up on Kinoiserie. An import keeps the source date separately
    /// and does not move again when a later scrape sees the same post.
    /// </summary>
    public static DateTime AppearedAt(Discussion discussion) =>
        discussion.SourceImportedAt ?? discussion.CreatedAt;

    public static DateTime SortDate(Discussion discussion) => AppearedAt(discussion);

    public static IQueryable<Discussion> OrderByPopular(IQueryable<Discussion> query) =>
        query.OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt);

    public static IEnumerable<Discussion> OrderByPopular(IEnumerable<Discussion> discussions) =>
        discussions.OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt);
}
