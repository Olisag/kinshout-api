namespace Kinshout.Api.Services;

public static class AdvertImageUrls
{
    public const int ThumbnailMaxWidth = 400;
    public const int DisplayMaxWidth = 1200;
    public const string ThumbnailSuffix = "_thumb";
    public const string DisplaySuffix = "_display";
    public const string VariantExtension = ".webp";

    public static bool IsKinshoutImage(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.StartsWith("/uploads/images/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/uploads/images/", StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeStoragePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.StartsWith('/'))
            return url;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            var path = absolute.AbsolutePath;
            return path.StartsWith("/uploads/images/", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        return null;
    }

    public static string? GetVariantPath(string? originalUrl, string suffix) =>
        TryReplaceExtension(NormalizeStoragePath(originalUrl), suffix + VariantExtension);

    public static string? GetThumbnailPath(string? originalUrl) =>
        GetVariantPath(originalUrl, ThumbnailSuffix);

    public static string? GetDisplayPath(string? originalUrl) =>
        GetVariantPath(originalUrl, DisplaySuffix);

    public static string ResolveListingUrl(string? originalUrl) =>
        GetThumbnailPath(originalUrl) ?? originalUrl ?? string.Empty;

    public static string ResolveDisplayUrl(string? originalUrl) =>
        GetDisplayPath(originalUrl) ?? originalUrl ?? string.Empty;

    public static IReadOnlyList<string> BuildListingUrls(IReadOnlyList<string> imageUrls) =>
        imageUrls.Select(ResolveListingUrl).ToList();

    public static IReadOnlyList<string> BuildDisplayUrls(IReadOnlyList<string> imageUrls) =>
        imageUrls.Select(ResolveDisplayUrl).ToList();

    public static IReadOnlyList<string> ToPublicUrls(IUploadUrlResolver uploadUrls, IReadOnlyList<string> urls) =>
        urls.Select(u => uploadUrls.ToPublicUrl(u) ?? u).ToList();

    private static string? TryReplaceExtension(string? storagePath, string newExtension)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            return null;

        var extension = Path.GetExtension(storagePath);
        if (string.IsNullOrEmpty(extension))
            return null;

        return storagePath[..^extension.Length] + newExtension;
    }
}
