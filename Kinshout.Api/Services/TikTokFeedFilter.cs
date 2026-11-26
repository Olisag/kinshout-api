using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

/// <summary>
/// A TikTok stays off the feeds until we have stored its video file.
/// </summary>
public static class TikTokFeedFilter
{
    public static IQueryable<Discussion> WherePlayable(IQueryable<Discussion> query) =>
        query.Where(d =>
            d.SourceProvider == null
            || d.SourceProvider != DiscussionSourceProvider.TikTok
            || (d.VideoUrlsJson != null && d.VideoUrlsJson.Contains("/uploads/videos/")));

    public static bool IsHiddenWithoutFile(Discussion discussion) =>
        string.Equals(discussion.SourceProvider, DiscussionSourceProvider.TikTok, StringComparison.OrdinalIgnoreCase)
        && !ImportedVideoStore.IsHostedFile(discussion.VideoUrlsJson);
}
