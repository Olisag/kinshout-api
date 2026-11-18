using System.Text.Json;

namespace Kinshout.Api.Services;

public static class DiscussionMediaHelper
{
    public const int MaxImages = 10;
    public const int MaxVideos = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<string> ParseUrlList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeUrlList(IReadOnlyList<string> urls) =>
        JsonSerializer.Serialize(urls.ToList(), JsonOptions);

    public static List<string> NormalizeUrls(
        IReadOnlyList<string>? urls,
        Guid userId,
        string folder,
        int maxCount,
        string itemLabel,
        IReadOnlyCollection<string>? keptUrls = null)
    {
        if (urls is null || urls.Count == 0)
            return [];

        var prefix = $"/uploads/{folder}/{userId:N}/";
        var normalized = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > maxCount)
            throw new ArgumentException($"Maximum {maxCount} {itemLabel} par discussion.");

        foreach (var url in normalized)
        {
            // URLs already on the discussion may belong to its author when a moderator edits it.
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && keptUrls?.Contains(url, StringComparer.OrdinalIgnoreCase) != true)
            {
                throw new ArgumentException(
                    $"Seuls vos fichiers téléversés sur Kinoiserie ({folder}) sont autorisés.");
            }
        }

        return normalized;
    }

    public static List<Dtos.DiscussionMediaDto> ToMediaDtos(
        IReadOnlyList<string> imageUrls,
        IReadOnlyList<string> videoUrls,
        IReadOnlyDictionary<string, Models.VideoAsset>? videoAssetsByStorageUrl = null)
    {
        var postersConsumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videoItems = new List<Dtos.DiscussionMediaDto>(videoUrls.Count);

        foreach (var storageUrl in videoUrls)
        {
            Models.VideoAsset? asset = null;
            videoAssetsByStorageUrl?.TryGetValue(storageUrl, out asset);

            if (asset is not null && asset.DeletedAt is null)
            {
                // Always expose preview URL; poster is created lazily if missing.
                var previewUrl = $"/api/videos/{asset.Id}/preview";
                var playUrl = $"/api/videos/{asset.Id}/stream";
                var status = asset.Status switch
                {
                    Models.VideoAssetStatus.Ready => null,
                    Models.VideoAssetStatus.Failed => Models.VideoAssetStatus.Failed,
                    _ => Models.VideoAssetStatus.Processing,
                };
                videoItems.Add(new Dtos.DiscussionMediaDto("video", previewUrl, previewUrl, playUrl, status));
            }
            else
            {
                // Hotlinked / imported clip (e.g. X): pair with a still poster when
                // available. Never set PreviewUrl to the MP4 — clients cannot paint it.
                // Keep Url as the playable stream so FE does not reclassify the row
                // as an image when PreviewUrl is a JPEG on pbs.twimg.com.
                var poster = TakeMatchingStillPoster(imageUrls, storageUrl, postersConsumed);
                videoItems.Add(new Dtos.DiscussionMediaDto(
                    "video",
                    storageUrl,
                    poster,
                    storageUrl));
            }
        }

        var items = new List<Dtos.DiscussionMediaDto>(imageUrls.Count + videoItems.Count);
        foreach (var url in imageUrls)
        {
            // Hide X/YouTube stills that were consumed as video PreviewUrl so the
            // feed does not show the same cover twice (gallery + player).
            if (postersConsumed.Contains(url))
                continue;
            if (videoItems.Count > 0 && DiscussionMultimedia.IsTwitterVideoPosterUrl(url))
                continue;
            items.Add(new Dtos.DiscussionMediaDto("image", url));
        }

        items.AddRange(videoItems);
        return items;
    }

    /// <summary>
    /// Prefer an unused X video_thumb / amplify thumb, else any remaining still
    /// that is not itself a playable MP4 (e.g. YouTube hqdefault).
    /// </summary>
    private static string? TakeMatchingStillPoster(
        IReadOnlyList<string> imageUrls,
        string videoUrl,
        HashSet<string> consumed)
    {
        string? fallback = null;
        foreach (var image in imageUrls)
        {
            if (consumed.Contains(image) || !DiscussionMultimedia.LooksLikeStillImageUrl(image))
                continue;

            if (DiscussionMultimedia.IsTwitterVideoPosterUrl(image))
            {
                consumed.Add(image);
                return image;
            }

            fallback ??= image;
        }

        if (fallback is not null)
            consumed.Add(fallback);

        // Avoid returning the playable URL itself as a "poster".
        if (fallback is not null
            && string.Equals(fallback, videoUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fallback;
    }

    /// <summary>Validates a single owned upload URL under /uploads/{folder}/{userId:N}/.</summary>
    public static string NormalizeOwnedUploadUrl(string url, Guid userId, string folder)
    {
        var trimmed = url.Trim();
        var prefix = $"/uploads/{folder}/{userId:N}/";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Seuls vos fichiers téléversés sur Kinoiserie ({folder}) sont autorisés.");
        }

        return trimmed;
    }
}
