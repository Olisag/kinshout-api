using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public interface IVideoService
{
    Task<VideoDto> UploadAsync(Guid userId, IFormFile video, IFormFile? poster = null, CancellationToken ct = default);
    /// <summary>
    /// Register a direct upload and return the URL the client uploads the source file to,
    /// or null when the storage cannot hand out upload URLs.
    /// </summary>
    Task<VideoUploadDto?> CreateUploadAsync(Guid userId, CreateVideoUploadRequestDto request, CancellationToken ct = default);
    /// <summary>Called once the source file is uploaded: queues processing (or publishes it as-is).</summary>
    Task<VideoDto> CompleteUploadAsync(
        Guid userId,
        Guid id,
        double? trimStartSeconds,
        double? trimEndSeconds,
        IFormFile? poster = null,
        CancellationToken ct = default);
    /// <summary>Apply the video worker's outcome. Returns false when the video does not exist.</summary>
    Task<bool> ApplyProcessingResultAsync(Guid id, VideoProcessingResultDto result, CancellationToken ct = default);
    Task<VideoDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<UploadFileContent?> OpenPreviewAsync(Guid id, CancellationToken ct = default);
    Task<UploadFileContent?> OpenStreamAsync(Guid id, CancellationToken ct = default);
    /// <summary>Direct storage URL for playback, or null when the API must stream the file.</summary>
    Task<Uri?> GetDirectStreamUriAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
    /// <summary>
    /// Deletes direct uploads that were never completed within <paramref name="olderThan"/>
    /// (tab closed mid-upload, post abandoned). Returns how many were deleted.
    /// </summary>
    Task<int> DeleteAbandonedUploadsAsync(TimeSpan olderThan, CancellationToken ct = default);
    /// <summary>Register legacy storage URLs as VideoAssets so feeds can always expose preview URLs.</summary>
    Task<IReadOnlyDictionary<string, VideoAsset>> EnsureAssetsForStorageUrlsAsync(
        IEnumerable<string> storageUrls,
        CancellationToken ct = default);
}

/// <summary>
/// Video pipeline: clients upload the source straight to storage, the video worker trims it to at
/// most two minutes and re-encodes it to 720p with a poster, and viewers get immutable long-cache
/// files with HTTP range support. Legacy multipart uploads are stored as-is.
/// Legacy videos without posters get a cheap generated placeholder on first preview.
/// </summary>
public class VideoService(
    KinshoutDbContext db,
    IUploadStorage storage,
    IAdvertImageProcessor imageProcessor,
    ILogger<VideoService> logger,
    IVideoProcessingQueue? processingQueue = null,
    IOptions<VideoProcessingSettings>? processingOptions = null) : IVideoService
{
    public const long MaxVideoBytes = 50 * 1024 * 1024;
    public const long MaxPosterBytes = 512 * 1024;
    // Keyframe snapping and container rounding can push an exact two-minute selection slightly over.
    private const double TrimToleranceSeconds = 1;
    private static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromHours(2);

    private readonly VideoProcessingSettings _processing = processingOptions?.Value ?? new VideoProcessingSettings();

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

    public async Task<VideoUploadDto?> CreateUploadAsync(
        Guid userId,
        CreateVideoUploadRequestDto request,
        CancellationToken ct = default)
    {
        if (request.ByteSize <= 0)
            throw new ArgumentException("Aucune vidéo reçue.");

        if (request.ByteSize > _processing.MaxSourceBytes)
            throw new ArgumentException(
                $"Vidéo trop volumineuse (max {_processing.MaxSourceBytes / (1024 * 1024)} Mo).");

        var extension = ValidateExtension(request.FileName, VideoExtensions, "vidéo");
        var fileId = Guid.NewGuid().ToString("N");
        var sourceUrl = LocalUploadStorage.BuildUrl(
            "videos", userId, $"{fileId}_source{extension.ToLowerInvariant()}");

        var uploadUri = await storage.GetDirectWriteUriAsync(sourceUrl, UploadUrlLifetime, ct);
        if (uploadUri is null)
            return null;

        var asset = new VideoAsset
        {
            UserId = userId,
            VideoUrl = LocalUploadStorage.BuildUrl("videos", userId, $"{fileId}.mp4"),
            SourceUrl = sourceUrl,
            ContentType = "video/mp4",
            ByteSize = request.ByteSize,
            OriginalFileName = Path.GetFileName(request.FileName),
            Status = VideoAssetStatus.Uploading,
        };
        db.VideoAssets.Add(asset);
        await db.SaveChangesAsync(ct);

        return new VideoUploadDto(
            asset.Id,
            uploadUri.AbsoluteUri,
            DateTime.UtcNow + UploadUrlLifetime,
            ToDto(asset));
    }

    public async Task<VideoDto> CompleteUploadAsync(
        Guid userId,
        Guid id,
        double? trimStartSeconds,
        double? trimEndSeconds,
        IFormFile? poster = null,
        CancellationToken ct = default)
    {
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct)
            ?? throw new KeyNotFoundException("Vidéo introuvable.");

        if (asset.UserId != userId)
            throw new UnauthorizedAccessException("Seul le propriétaire peut finaliser cette vidéo.");

        // Retried requests (flaky mobile networks) must not queue the same video twice.
        if (asset.Status != VideoAssetStatus.Uploading || asset.SourceUrl is null)
            return ToDto(asset);

        var length = await storage.GetLengthAsync(asset.SourceUrl, ct)
            ?? throw new ArgumentException("La vidéo n'a pas été reçue. Réessayez.");

        if (length > _processing.MaxSourceBytes)
        {
            await storage.DeleteIfExistsAsync(asset.SourceUrl, ct);
            throw new ArgumentException(
                $"Vidéo trop volumineuse (max {_processing.MaxSourceBytes / (1024 * 1024)} Mo).");
        }

        ValidateTrim(trimStartSeconds, trimEndSeconds);

        if (poster is not null && poster.Length > 0)
        {
            asset.PosterUrl = await SavePosterFromUploadAsync(
                asset.UserId,
                Path.GetFileNameWithoutExtension(asset.VideoUrl),
                poster,
                ct);
        }

        asset.ByteSize = length;
        asset.TrimStartSeconds = trimStartSeconds;
        asset.TrimEndSeconds = trimEndSeconds;

        if (_processing.Enabled && processingQueue is not null)
        {
            asset.Status = VideoAssetStatus.Processing;
            await db.SaveChangesAsync(ct);

            try
            {
                await processingQueue.EnqueueAsync(
                    new VideoProcessingJob(
                        asset.Id,
                        asset.SourceUrl,
                        asset.VideoUrl,
                        GetProcessedPosterUrl(asset),
                        trimStartSeconds,
                        trimEndSeconds,
                        _processing.MaxDurationSeconds),
                    ct);

                logger.LogInformation("Queued video asset {VideoId} for processing", asset.Id);
                return ToDto(asset);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Better an unprocessed video than a lost post.
                logger.LogError(ex, "Could not queue video asset {VideoId}; publishing the source as-is", asset.Id);
            }
        }

        await PublishSourceAsIsAsync(asset, ct);
        return ToDto(asset);
    }

    public async Task<bool> ApplyProcessingResultAsync(
        Guid id,
        VideoProcessingResultDto result,
        CancellationToken ct = default)
    {
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (asset is null)
            return false;

        if (asset.Status == VideoAssetStatus.Ready)
            return true;

        var processedLength = result.Succeeded
            ? await storage.GetLengthAsync(asset.VideoUrl, ct)
            : null;

        if (processedLength is null)
        {
            asset.Status = VideoAssetStatus.Failed;
            asset.ProcessingError = Truncate(
                result.Succeeded ? "Processed video is missing from storage." : result.Error ?? "Unknown error.",
                500);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Video asset {VideoId} failed processing: {Error}", asset.Id, asset.ProcessingError);
            return true;
        }

        var processedPosterUrl = GetProcessedPosterUrl(asset);
        if (await storage.ExistsAsync(processedPosterUrl, ct))
        {
            if (!string.IsNullOrWhiteSpace(asset.PosterUrl) &&
                !asset.PosterUrl.Equals(processedPosterUrl, StringComparison.OrdinalIgnoreCase))
            {
                await storage.DeleteIfExistsAsync(asset.PosterUrl, ct);
            }

            asset.PosterUrl = processedPosterUrl;
        }

        if (asset.SourceUrl is not null)
            await storage.DeleteIfExistsAsync(asset.SourceUrl, ct);

        asset.SourceUrl = null;
        asset.ContentType = "video/mp4";
        asset.ByteSize = processedLength.Value;
        asset.DurationSeconds = result.DurationSeconds;
        asset.Width = result.Width;
        asset.Height = result.Height;
        asset.ProcessingError = null;
        asset.Status = VideoAssetStatus.Ready;
        await db.SaveChangesAsync(ct);

        if (asset.PosterUrl is null)
            await EnsurePosterAsync(asset, ct);

        logger.LogInformation(
            "Video asset {VideoId} processed ({Bytes} bytes, {Duration}s)",
            asset.Id,
            asset.ByteSize,
            asset.DurationSeconds);
        return true;
    }

    public async Task<VideoDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (asset is null)
            return null;

        if (asset.PosterUrl is null && asset.Status == VideoAssetStatus.Ready)
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
            .FirstOrDefaultAsync(
                v => v.Id == id && v.DeletedAt == null && v.Status == VideoAssetStatus.Ready,
                ct);
        if (asset is null)
            return null;

        var file = await storage.OpenReadAsync(asset.VideoUrl, ct);
        if (file is null)
            return null;

        return new UploadFileContent(file.Stream, asset.ContentType);
    }

    public async Task<Uri?> GetDirectStreamUriAsync(Guid id, CancellationToken ct = default)
    {
        var videoUrl = await db.VideoAssets.AsNoTracking()
            .Where(v => v.Id == id && v.DeletedAt == null && v.Status == VideoAssetStatus.Ready)
            .Select(v => v.VideoUrl)
            .FirstOrDefaultAsync(ct);

        return videoUrl is null ? null : await storage.GetDirectReadUriAsync(videoUrl, ct);
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
        if (!string.IsNullOrWhiteSpace(asset.SourceUrl))
            await storage.DeleteIfExistsAsync(asset.SourceUrl, ct);

        asset.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteAbandonedUploadsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        const int batchSize = 100;
        var cutoff = DateTime.UtcNow - olderThan;
        var deleted = 0;

        while (true)
        {
            var abandoned = await db.VideoAssets
                .Where(v => v.Status == VideoAssetStatus.Uploading && v.DeletedAt == null && v.CreatedAt < cutoff)
                .OrderBy(v => v.CreatedAt)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var asset in abandoned)
            {
                if (!string.IsNullOrWhiteSpace(asset.SourceUrl))
                    await storage.DeleteIfExistsAsync(asset.SourceUrl, ct);

                asset.DeletedAt = DateTime.UtcNow;
            }

            if (abandoned.Count > 0)
                await db.SaveChangesAsync(ct);

            deleted += abandoned.Count;

            if (abandoned.Count < batchSize)
                break;
        }

        if (deleted > 0)
            logger.LogInformation("Deleted {Count} abandoned video uploads", deleted);

        return deleted;
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

    private void ValidateTrim(double? start, double? end)
    {
        if (start is < 0 || end is <= 0)
            throw new ArgumentException("Sélection de la vidéo invalide.");

        if (start is not null && end is not null)
        {
            if (end <= start)
                throw new ArgumentException("Sélection de la vidéo invalide.");

            if (end - start > _processing.MaxDurationSeconds + TrimToleranceSeconds)
                throw new ArgumentException(
                    $"La vidéo doit durer {_processing.MaxDurationSeconds / 60:0.#} minutes maximum.");
        }
    }

    private async Task PublishSourceAsIsAsync(VideoAsset asset, CancellationToken ct)
    {
        asset.VideoUrl = asset.SourceUrl!;
        asset.ContentType = ContentTypeForExtension(Path.GetExtension(asset.SourceUrl!));
        asset.SourceUrl = null;
        asset.Status = VideoAssetStatus.Ready;
        await db.SaveChangesAsync(ct);

        if (asset.PosterUrl is null)
            await EnsurePosterAsync(asset, ct);
    }

    private static string GetProcessedPosterUrl(VideoAsset asset)
    {
        var directory = asset.VideoUrl[..(asset.VideoUrl.LastIndexOf('/') + 1)];
        return $"{directory}{Path.GetFileNameWithoutExtension(asset.VideoUrl)}_poster.jpg";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

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
            asset.UserId,
            asset.Status,
            asset.DurationSeconds,
            asset.Width,
            asset.Height,
            asset.Status == VideoAssetStatus.Failed ? asset.ProcessingError : null);

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
