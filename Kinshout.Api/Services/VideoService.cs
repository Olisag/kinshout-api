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
}

/// <summary>
/// Cost-efficient Reddit-like video pipeline:
/// upload original once (no cloud transcoder), optional compressed poster for feeds,
/// immutable long-cache + HTTP range streaming for viewers.
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
            posterUrl = await SavePosterAsync(userId, fileId, poster, ct);

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

        logger.LogInformation(
            "Stored video asset {VideoId} ({Bytes} bytes) for user {UserId}",
            asset.Id,
            asset.ByteSize,
            userId);

        return ToDto(asset);
    }

    public async Task<VideoDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        return asset is null ? null : ToDto(asset);
    }

    public async Task<UploadFileContent?> OpenPreviewAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (asset?.PosterUrl is null)
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

        // Prefer DB content type over path sniffing so players get correct MIME.
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

    private async Task<string> SavePosterAsync(Guid userId, string fileId, IFormFile poster, CancellationToken ct)
    {
        if (poster.Length > MaxPosterBytes)
            throw new ArgumentException($"Aperçu trop volumineux (max {MaxPosterBytes / 1024} Ko).");

        ValidateExtension(poster.FileName, PosterExtensions, "aperçu");

        await using var buffer = new MemoryStream();
        await poster.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        // Compress to a small WebP thumb — cheap for feeds, one-time CPU at upload.
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

    private static VideoDto ToDto(VideoAsset asset) =>
        new(
            asset.Id,
            PlayUrl: $"/api/videos/{asset.Id}/stream",
            PreviewUrl: asset.PosterUrl is null ? null : $"/api/videos/{asset.Id}/preview",
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
