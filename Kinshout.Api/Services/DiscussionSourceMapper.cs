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
}
