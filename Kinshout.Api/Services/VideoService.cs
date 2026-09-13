using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IVideoService
{
    Task<VideoDto> UploadAsync(Guid userId, IFormFile video, IFormFile? poster = null, CancellationToken ct = default);
    Task<VideoDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<UploadFileContent?> OpenPreviewAsync(Guid id, CancellationToken ct = default);
    Task<UploadFileContent?> OpenStreamAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
    /// <summary>Register legacy storage URLs as VideoAssets so feeds can always expose preview URLs.</summary>
    Task<IReadOnlyDictionary<string, VideoAsset>> EnsureAssetsForStorageUrlsAsync(
        IEnumerable<string> storageUrls,
        CancellationToken ct = default);
}

/// <summary>
/// Cost-efficient Reddit-like video pipeline:
/// upload original once (no cloud transcoder), optional compressed poster for feeds,
/// immutable long-cache + HTTP range streaming for viewers.
/// Legacy videos without posters get a cheap generated placeholder on first preview.
/// </summary>
public class VideoService(
    KinshoutDbContext db,
    IUploadStorage storage,
    IAdvertImageProcessor imageProcessor,
    ILogger<VideoService> logger) : IVideoService
{
    public const long MaxVideoBytes = 50 * 1024 * 1024;
    public const long MaxPosterBytes = 512 * 1024;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov",
    };

    private static readonly HashSet<string> PosterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    public async Task<VideoDto> UploadAsync(
        Guid userId,
        IFormFile video,
        IFormFile? poster = null,
        CancellationToken ct = default)
    {
        if (video is null || video.Length == 0)
            throw new ArgumentException("Aucune vidéo reçue.");

        if (video.Length > MaxVideoBytes)
            throw new ArgumentException($"Vidéo trop volumineuse (max {MaxVideoBytes / (1024 * 1024)} Mo).");

        var extension = ValidateExtension(video.FileName, VideoExtensions, "vidéo");
        var contentType = ContentTypeForExtension(extension);
        var fileId = Guid.NewGuid().ToString("N");
        var fileName = $"{fileId}{extension.ToLowerInvariant()}";

        await using var videoBuffer = new MemoryStream();
        await video.CopyToAsync(videoBuffer, ct);
        videoBuffer.Position = 0;
        var videoUrl = await storage.SaveNamedAsync("videos", userId, videoBuffer, fileName, ct);

        string? posterUrl = null;
        if (poster is not null && poster.Length > 0)
            posterUrl = await SavePosterFromUploadAsync(userId, fileId, poster, ct);

        var asset = new VideoAsset
        {
            UserId = userId,
            VideoUrl = videoUrl,
            PosterUrl = posterUrl,
            ContentType = contentType,
            ByteSize = video.Length,
            OriginalFileName = Path.GetFileName(video.FileName),
        };
        db.VideoAssets.Add(asset);
        await db.SaveChangesAsync(ct);

        if (asset.PosterUrl is null)
            await EnsurePosterAsync(asset, ct);

        logger.LogInformation(
            "Stored video asset {VideoId} ({Bytes} bytes) for user {UserId}",
            asset.Id,
            asset.ByteSize,
            userId);

        return ToDto(asset);
    }

    public async Task<VideoDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (asset is null)
            return null;

        if (asset.PosterUrl is null)
            await EnsurePosterAsync(asset, ct);

        return ToDto(asset);
    }

    public async Task<UploadFileContent?> OpenPreviewAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (asset is null)
            return null;

        if (asset.PosterUrl is null)
            await EnsurePosterAsync(asset, ct);

        if (asset.PosterUrl is null)
            return null;

        return await storage.OpenReadAsync(asset.PosterUrl, ct);
    }

    public async Task<UploadFileContent?> OpenStreamAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (asset is null)
            return null;

        var file = await storage.OpenReadAsync(asset.VideoUrl, ct);
        if (file is null)
            return null;

        return new UploadFileContent(file.Stream, asset.ContentType);
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct)
            ?? throw new KeyNotFoundException("Vidéo introuvable.");

        if (asset.UserId != userId)
            throw new UnauthorizedAccessException("Seul le propriétaire peut supprimer cette vidéo.");

        await storage.DeleteIfExistsAsync(asset.VideoUrl, ct);
        if (!string.IsNullOrWhiteSpace(asset.PosterUrl))
            await storage.DeleteIfExistsAsync(asset.PosterUrl, ct);

        asset.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, VideoAsset>> EnsureAssetsForStorageUrlsAsync(
        IEnumerable<string> storageUrls,
        CancellationToken ct = default)
    {
        var urls = storageUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
            return new Dictionary<string, VideoAsset>(StringComparer.OrdinalIgnoreCase);

        var existing = await db.VideoAssets
            .Where(v => v.DeletedAt == null && urls.Contains(v.VideoUrl))
            .ToListAsync(ct);

        var byUrl = existing
            .GroupBy(v => v.VideoUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var created = false;
        foreach (var url in urls)
        {
            if (byUrl.ContainsKey(url))
                continue;

            if (!TryParseVideoStorageUrl(url, out var ownerId, out var fileName))
                continue;

            if (!await storage.ExistsAsync(url, ct))
                continue;

            var asset = new VideoAsset
            {
                UserId = ownerId,
                VideoUrl = url,
                ContentType = ContentTypeForExtension(Path.GetExtension(fileName)),
                ByteSize = 0,
                OriginalFileName = fileName,
            };
            db.VideoAssets.Add(asset);
            byUrl[url] = asset;
            created = true;
        }

        if (created)
            await db.SaveChangesAsync(ct);

        return byUrl;
    }

    private async Task EnsurePosterAsync(VideoAsset asset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(asset.PosterUrl))
            return;

        var fileId = Path.GetFileNameWithoutExtension(asset.VideoUrl);
        if (string.IsNullOrWhiteSpace(fileId))
            fileId = asset.Id.ToString("N");

        await using var poster = await VideoPosterGenerator.CreatePlaceholderAsync(ct);
        var posterName = $"{fileId}_poster{AdvertImageUrls.VariantExtension}";
        asset.PosterUrl = await storage.SaveNamedAsync("videos", asset.UserId, poster, posterName, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Generated placeholder poster for video asset {VideoId}", asset.Id);
    }

    private async Task<string> SavePosterFromUploadAsync(
        Guid userId,
        string fileId,
        IFormFile poster,
        CancellationToken ct)
    {
        if (poster.Length > MaxPosterBytes)
            throw new ArgumentException($"Aperçu trop volumineux (max {MaxPosterBytes / 1024} Ko).");

        ValidateExtension(poster.FileName, PosterExtensions, "aperçu");

        await using var buffer = new MemoryStream();
        await poster.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        await using var thumb = await imageProcessor.CreateListingThumbnailAsync(buffer, ct);
        if (thumb is not null)
        {
            var thumbName = $"{fileId}_poster{AdvertImageUrls.VariantExtension}";
            return await storage.SaveNamedAsync("videos", userId, thumb, thumbName, ct);
        }

        buffer.Position = 0;
        var ext = Path.GetExtension(poster.FileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".jpg";
        var fileName = $"{fileId}_poster{ext}";
        return await storage.SaveNamedAsync("videos", userId, buffer, fileName, ct);
    }

    internal static bool TryParseVideoStorageUrl(string url, out Guid userId, out string fileName)
    {
        userId = default;
        fileName = "";
        // /uploads/videos/{userId:N}/{file}
        var parts = url.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return false;
        if (!parts[0].Equals("uploads", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!parts[1].Equals("videos", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Guid.TryParseExact(parts[2], "N", out userId))
            return false;

        fileName = parts[^1];
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static VideoDto ToDto(VideoAsset asset) =>
        new(
            asset.Id,
            PlayUrl: $"/api/videos/{asset.Id}/stream",
            PreviewUrl: $"/api/videos/{asset.Id}/preview",
            StorageUrl: asset.VideoUrl,
            asset.ContentType,
            asset.ByteSize,
            asset.OriginalFileName,
            asset.CreatedAt,
            asset.UserId);

    private static string ValidateExtension(string fileName, HashSet<string> allowed, string kind)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || !allowed.Contains(extension))
            throw new ArgumentException(
                $"Format de {kind} non supporté. Autorisés : {string.Join(", ", allowed)}.");

        return extension;
    }

    private static string ContentTypeForExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "video/mp4",
        };
}
