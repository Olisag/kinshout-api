using System.Text.RegularExpressions;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

/// <summary>
/// Visual-first rules for the social feed (X-like): photo/video or a concrete YouTube watch URL.
/// Text-only posts are allowed only when engagement is an absolute hit.
/// </summary>
public static partial class DiscussionMultimedia
{
    /// <summary>Source engagement score above which a text-only viral post may stay.</summary>
    public const int AbsoluteHitEngagementScore = 2500;

    /// <summary>On-platform likes that also qualify a text-only post as a hit.</summary>
    public const int AbsoluteHitLikeCount = 40;

    private static readonly Regex UrlRegex = CreateUrlRegex();

    public static bool HasVisualMedia(Discussion discussion)
    {
        var images = DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson);
        if (images.Count > 0)
            return true;

        var videos = DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson)
            .Where(url => !IsUnplayableHotlinkedVideo(url))
            .ToList();
        if (videos.Count > 0)
            return true;

        if (HasConcreteYouTubeUrl(discussion.SourceExternalUrl)
            || HasConcreteYouTubeUrl(discussion.Body)
            || HasConcreteYouTubeUrl(discussion.SourceRawBody))
        {
            return true;
        }

        return false;
    }

    /// <summary>Facebook/Instagram CDN video deep-links usually 403 when hotlinked.</summary>
    public static bool IsUnplayableHotlinkedVideo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (!(host.EndsWith("fbcdn.net", StringComparison.Ordinal)
            || host.EndsWith("facebook.com", StringComparison.Ordinal)
            || host.EndsWith("cdninstagram.com", StringComparison.Ordinal)
            || host.EndsWith("instagram.com", StringComparison.Ordinal)))
        {
            return false;
        }

        return url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mov", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".webm", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/v/t2/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAbsoluteHit(Discussion discussion) =>
        (discussion.SourceEngagementScore ?? 0) >= AbsoluteHitEngagementScore
        || discussion.LikeCount >= AbsoluteHitLikeCount;

    public static bool ShouldKeepOnVisualFeed(Discussion discussion)
    {
        if (HasVisualMedia(discussion))
            return true;

        // Absolute-hit exception is for true text virals only — not broken FB CDN videos.
        if (HasOnlyUnplayableHotlinkedVideo(discussion))
            return false;

        return IsAbsoluteHit(discussion);
    }

    public static bool HasOnlyUnplayableHotlinkedVideo(Discussion discussion)
    {
        var images = DiscussionMediaHelper.ParseUrlList(discussion.ImageUrlsJson);
        if (images.Count > 0)
            return false;

        if (HasConcreteYouTubeUrl(discussion.SourceExternalUrl)
            || HasConcreteYouTubeUrl(discussion.Body)
            || HasConcreteYouTubeUrl(discussion.SourceRawBody))
        {
            return false;
        }

        var videos = DiscussionMediaHelper.ParseUrlList(discussion.VideoUrlsJson);
        return videos.Count > 0 && videos.All(IsUnplayableHotlinkedVideo);
    }

    public static bool ImportItemHasVisualMedia(
        IReadOnlyList<string>? imageUrls,
        IReadOnlyList<string>? videoUrls,
        string? body,
        string? externalUrl)
    {
        if (imageUrls is { Count: > 0 } || videoUrls is { Count: > 0 })
            return true;

        return HasConcreteYouTubeUrl(externalUrl) || HasConcreteYouTubeUrl(body);
    }

    public static bool IsAbsoluteHitEngagement(int? engagementScore) =>
        engagementScore.GetValueOrDefault() >= AbsoluteHitEngagementScore;

    public static bool HasConcreteYouTubeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (Match match in UrlRegex.Matches(text))
        {
            var url = match.Value.TrimEnd(')', ']', '.', ',', ';', '"', '\'');
            if (TryGetYouTubeVideoId(url, out _))
                return true;
        }

        return false;
    }

    public static bool TryGetYouTubeVideoId(string url, out string videoId)
    {
        videoId = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (IsVideoId(id))
            {
                videoId = id;
                return true;
            }

            return false;
        }

        if (!host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (path.StartsWith("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var id = ExtractQueryValue(uri.Query, "v");
            if (IsVideoId(id))
            {
                videoId = id!;
                return true;
            }

            return false;
        }

        if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/v/", StringComparison.OrdinalIgnoreCase))
        {
            var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            if (IsVideoId(id))
            {
                videoId = id!;
                return true;
            }
        }

        return false;
    }

    private static bool IsVideoId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length == 11
        && !id.Equals("example", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair[1]))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    [GeneratedRegex(@"https?://[^\s<>""'`\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateUrlRegex();
}
