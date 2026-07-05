using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public interface IExternalAdvertImageMirrorService
{
    Task<IReadOnlyList<string>> MirrorAsync(
        IReadOnlyList<string>? sourceUrls,
        string provider,
        string externalId,
        Guid ownerUserId,
        CancellationToken ct = default);

    Task DeleteMirroredAsync(IReadOnlyList<string> storedUrls, CancellationToken ct = default);
}

public sealed class ExternalAdvertImageMirrorService(
    IHttpClientFactory httpClientFactory,
    IUploadStorage storage,
    IAdvertImageProcessor imageProcessor,
    IOptions<ImportSettings> importOptions,
    ILogger<ExternalAdvertImageMirrorService> logger) : IExternalAdvertImageMirrorService
{
    private const int MaxImages = 10;
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif",
    };

    public async Task<IReadOnlyList<string>> MirrorAsync(
        IReadOnlyList<string>? sourceUrls,
        string provider,
        string externalId,
        Guid ownerUserId,
        CancellationToken ct = default)
    {
        if (sourceUrls is null || sourceUrls.Count == 0)
            return [];

        if (!importOptions.Value.MirrorExternalImages)
            return sourceUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Take(MaxImages).ToList();

        var results = new List<string>();
        var index = 0;
        foreach (var raw in sourceUrls)
        {
            if (index >= MaxImages)
                break;

            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var source = raw.Trim();
            var existingPath = AdvertImageUrls.NormalizeStoragePath(source);
            if (existingPath is not null)
            {
                results.Add(existingPath);
                index++;
                continue;
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            try
            {
                var mirrored = await MirrorOneAsync(uri, provider, externalId, ownerUserId, index, ct);
                if (mirrored is not null)
                {
                    results.Add(mirrored);
                    index++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to mirror import image {Url} for {Provider}/{ExternalId}", source, provider, externalId);
            }
        }

        return results;
    }

    public async Task DeleteMirroredAsync(IReadOnlyList<string> storedUrls, CancellationToken ct = default)
    {
        foreach (var url in storedUrls)
        {
            var path = AdvertImageUrls.NormalizeStoragePath(url);
            if (path is null)
                continue;

            await storage.DeleteIfExistsAsync(path, ct);
            var thumb = AdvertImageUrls.GetThumbnailPath(path);
            if (thumb is not null)
                await storage.DeleteIfExistsAsync(thumb, ct);
            var display = AdvertImageUrls.GetDisplayPath(path);
            if (display is not null)
                await storage.DeleteIfExistsAsync(display, ct);
        }
    }

    private async Task<string?> MirrorOneAsync(
        Uri sourceUri,
        string provider,
        string externalId,
        Guid ownerUserId,
        int index,
        CancellationToken ct)
    {
        var extension = GuessExtension(sourceUri);
        var baseName = BuildBaseFileName(provider, externalId, index, extension);
        var storagePath = LocalUploadStorage.BuildUrl("images", ownerUserId, baseName);

        if (await storage.ExistsAsync(storagePath, ct))
            return storagePath;

        var client = httpClientFactory.CreateClient("ExternalImageMirror");
        using var response = await client.GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
        await using var buffer = new MemoryStream();
        await CopyLimitedAsync(networkStream, buffer, MaxImageBytes, ct);
        if (buffer.Length == 0)
            return null;

        buffer.Position = 0;
        var savedPath = await storage.SaveNamedAsync("images", ownerUserId, buffer, baseName, ct);

        buffer.Position = 0;
        await using var thumbnail = await imageProcessor.CreateListingThumbnailAsync(buffer, ct);
        if (thumbnail is not null)
        {
            var thumbName = BuildVariantFileName(baseName, AdvertImageUrls.ThumbnailSuffix);
            await storage.SaveNamedAsync("images", ownerUserId, thumbnail, thumbName, ct);
        }

        buffer.Position = 0;
        await using var display = await imageProcessor.CreateDisplayImageAsync(buffer, ct);
        if (display is not null)
        {
            var displayName = BuildVariantFileName(baseName, AdvertImageUrls.DisplaySuffix);
            await storage.SaveNamedAsync("images", ownerUserId, display, displayName, ct);
        }

        logger.LogInformation(
            "Mirrored import image {Source} -> {Path} ({Provider}/{ExternalId})",
            sourceUri,
            savedPath,
            provider,
            externalId);

        return savedPath;
    }

    private static string BuildVariantFileName(string baseFileName, string suffix)
    {
        var extension = Path.GetExtension(baseFileName);
        var stem = baseFileName[..^extension.Length];
        return $"{stem}{suffix}{AdvertImageUrls.VariantExtension}";
    }

    internal static string BuildBaseFileName(string provider, string externalId, int index, string extension)
    {
        var safeProvider = SanitizeKey(provider);
        var safeExternalId = SanitizeKey(externalId);
        if (string.IsNullOrEmpty(safeExternalId))
            safeExternalId = "item";

        return $"{safeProvider}_{safeExternalId}_{index}{extension.ToLowerInvariant()}";
    }

    private static string SanitizeKey(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string GuessExtension(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (!string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext))
            return ext;

        return ".jpg";
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException("Image exceeds size limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
