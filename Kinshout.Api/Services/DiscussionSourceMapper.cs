using Kinshout.Api.Dtos;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

internal static class DiscussionSourceMapper
{
    public static bool IsSameContent(Discussion existing, Discussion mapped) =>
        existing.Title == mapped.Title
        && existing.Body == mapped.Body
        && existing.SourceEngagementScore == mapped.SourceEngagementScore;

    public static DiscussionSourceDto? ToSourceDto(Discussion discussion)
    {
        if (!discussion.IsExternal || string.IsNullOrWhiteSpace(discussion.SourceProvider))
            return null;

        return new DiscussionSourceDto(
            discussion.SourceProvider,
            discussion.SourceProviderName ?? DiscussionSourceProvider.DisplayName(discussion.SourceProvider),
            discussion.SourceExternalId ?? string.Empty,
            discussion.SourceExternalUrl ?? string.Empty,
            discussion.SourceImportedAt ?? discussion.CreatedAt,
            discussion.SourceLastSeenAt ?? discussion.UpdatedAt,
            discussion.SourceFirstSeenAt ?? discussion.CreatedAt,
            discussion.SourceOriginalAuthor,
            discussion.SourceEngagementScore,
            discussion.ExternalPublishedAt);
    }

    public static DateTime SortDate(Discussion discussion) =>
        discussion.ExternalPublishedAt ?? discussion.CreatedAt;

    public static IQueryable<Discussion> OrderByPopular(IQueryable<Discussion> query) =>
        query.OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt);

    public static IEnumerable<Discussion> OrderByPopular(IEnumerable<Discussion> discussions) =>
        discussions.OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt);
}
