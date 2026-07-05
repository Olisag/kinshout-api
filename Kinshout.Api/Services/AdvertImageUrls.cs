namespace Kinshout.Api.Services;

public static class AdvertImageUrls
{
    public const int ThumbnailMaxWidth = 400;
    public const string ThumbnailSuffix = "_thumb";
    public const string ThumbnailExtension = ".webp";

    public static bool IsKinshoutImage(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.StartsWith("/uploads/images/", StringComparison.OrdinalIgnoreCase);

    public static string? GetThumbnailPath(string? originalUrl)
    {
        if (!IsKinshoutImage(originalUrl))
            return null;

        var extension = Path.GetExtension(originalUrl!);
        if (string.IsNullOrEmpty(extension))
            return null;

        return originalUrl![..^extension.Length] + ThumbnailSuffix + ThumbnailExtension;
    }

    public static string ResolveListingUrl(string? originalUrl) =>
        GetThumbnailPath(originalUrl) ?? originalUrl ?? string.Empty;

    public static IReadOnlyList<string> BuildListingUrls(IReadOnlyList<string> imageUrls) =>
        imageUrls.Select(ResolveListingUrl).ToList();
}
