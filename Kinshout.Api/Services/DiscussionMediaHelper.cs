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
        string itemLabel)
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
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Seuls vos fichiers téléversés sur Kinoiserie ({folder}) sont autorisés.");
            }
        }

        return normalized;
    }

    public static List<Dtos.DiscussionMediaDto> ToMediaDtos(
        IReadOnlyList<string> imageUrls,
        IReadOnlyList<string> videoUrls)
    {
        var items = new List<Dtos.DiscussionMediaDto>(imageUrls.Count + videoUrls.Count);
        items.AddRange(imageUrls.Select(url => new Dtos.DiscussionMediaDto("image", url)));
        items.AddRange(videoUrls.Select(url => new Dtos.DiscussionMediaDto("video", url)));
        return items;
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
