using System.Text.Json;

namespace Kinshout.Api.Services;

/// <summary>
/// One key per clip, so the same video posted on YouTube and again on Facebook is stored once.
/// </summary>
public static class MediaCanonicalKey
{
    public static string? From(string? externalUrl, IEnumerable<string>? videoUrls)
    {
        var externalKey = FromUrl(externalUrl);
        if (externalKey is not null && !externalKey.StartsWith("fb:", StringComparison.Ordinal))
            return externalKey;

        if (videoUrls is null)
            return externalKey;

        foreach (var url in videoUrls)
        {
            var key = FromUrl(url);
            // A Facebook share identifies the wrapper post, not necessarily the clip.
            // Prefer the attached clip's identity so a YouTube import deduplicates it.
            if (key is not null && (externalKey is null || !key.StartsWith("fb:", StringComparison.Ordinal)))
                return key;
        }

        return externalKey;
    }

    public static string? FromStored(string? externalUrl, string? videoUrlsJson)
    {
        List<string>? videos = null;
        if (!string.IsNullOrWhiteSpace(videoUrlsJson))
        {
            try
            {
                videos = JsonSerializer.Deserialize<List<string>>(videoUrlsJson);
            }
            catch (JsonException)
            {
                videos = null;
            }
        }

        return From(externalUrl, videos);
    }

    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        host = host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        if (host is "youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var watchId = Query(uri, "v");
            if (IsYouTubeId(watchId))
                return "yt:" + watchId;

            var nested = SegmentAfter(path, "shorts")
                ?? SegmentAfter(path, "embed")
                ?? SegmentAfter(path, "live");
            if (IsYouTubeId(nested))
                return "yt:" + nested;
        }

        if (host == "youtu.be")
        {
            var id = path.Trim('/');
            if (IsYouTubeId(id))
                return "yt:" + id;
        }

        if (host.EndsWith("tiktok.com", StringComparison.Ordinal))
        {
            var id = SegmentAfter(path, "video");
            if (id is { Length: >= 6 } && id.All(char.IsDigit))
                return "tt:" + id;
        }

        if (host is "x.com" or "twitter.com" or "mobile.twitter.com")
        {
            var id = SegmentAfter(path, "status");
            if (id is { Length: >= 6 } && id.All(char.IsDigit))
                return "x:" + id;
        }

        if (host.Contains("facebook.com", StringComparison.Ordinal) || host == "fb.watch")
        {
            var story = Query(uri, "story_fbid");
            if (!string.IsNullOrWhiteSpace(story))
                return "fb:" + story;

            var video = Query(uri, "v");
            if (!string.IsNullOrWhiteSpace(video))
                return "fb:" + video;

            var nested = SegmentAfter(path, "videos")
                ?? SegmentAfter(path, "posts")
                ?? SegmentAfter(path, "reel")
                ?? SegmentAfter(path, "watch");
            if (!string.IsNullOrWhiteSpace(nested))
                return "fb:" + nested;
        }

        return null;
    }

    private static bool IsYouTubeId(string? value) =>
        value is { Length: >= 6 and <= 20 }
        && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

    private static string? Query(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(pair[1]).Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? SegmentAfter(string path, string marker)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (string.Equals(parts[index], marker, StringComparison.OrdinalIgnoreCase))
                return parts[index + 1];
        }

        return null;
    }
}
