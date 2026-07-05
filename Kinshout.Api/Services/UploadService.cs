using Kinshout.Api.Dtos;

namespace Kinshout.Api.Services;

public interface IUploadService
{
    Task<IReadOnlyList<string>> SaveImagesAsync(Guid userId, IFormFileCollection files, CancellationToken ct = default);
    Task<string> SaveAvatarAsync(Guid userId, IFormFile file, CancellationToken ct = default);
    Task<string> SaveResumeAsync(Guid userId, IFormFile file, CancellationToken ct = default);
}

public class UploadService(
    IUploadStorage storage,
    IAdvertImageProcessor imageProcessor,
    IAdvertModerationService moderation,
    ILogger<UploadService> logger) : IUploadService
{
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private const long MaxAvatarBytes = 2 * 1024 * 1024;
    private const long MaxResumeBytes = 10 * 1024 * 1024;
    private const int MaxImages = 10;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    private static readonly HashSet<string> ResumeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx",
    };

    public async Task<IReadOnlyList<string>> SaveImagesAsync(
        Guid userId,
        IFormFileCollection files,
        CancellationToken ct = default)
    {
        if (files.Count == 0)
            throw new ArgumentException("Aucune image reçue.");

        if (files.Count > MaxImages)
            throw new ArgumentException($"Maximum {MaxImages} images par annonce.");

        var urls = new List<string>();
        foreach (var file in files)
        {
            await using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            ValidateSize(buffer.Length, MaxImageBytes);

            buffer.Position = 0;
            await moderation.EnsureImageAllowedAsync(buffer, file.ContentType ?? "image/jpeg", ct);

            buffer.Position = 0;
            var extension = ValidateExtension(file.FileName, ImageExtensions);
            var fileId = Guid.NewGuid().ToString("N");
            var fileName = $"{fileId}{extension.ToLowerInvariant()}";
            var url = await storage.SaveNamedAsync("images", userId, buffer, fileName, ct);
            logger.LogInformation("Stored image upload {Url} for user {UserId}", url, userId);

            buffer.Position = 0;
            await using var thumbnail = await imageProcessor.CreateListingThumbnailAsync(buffer, ct);
            if (thumbnail is not null)
            {
                var thumbName = $"{fileId}{AdvertImageUrls.ThumbnailSuffix}{AdvertImageUrls.ThumbnailExtension}";
                await storage.SaveNamedAsync("images", userId, thumbnail, thumbName, ct);
            }

            urls.Add(url);
        }

        return urls;
    }

    public async Task<string> SaveAvatarAsync(Guid userId, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0)
            throw new ArgumentException("Aucune image reçue.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        ValidateSize(buffer.Length, MaxAvatarBytes);

        buffer.Position = 0;
        await moderation.EnsureImageAllowedAsync(buffer, file.ContentType ?? "image/jpeg", ct);

        buffer.Position = 0;
        var extension = ValidateExtension(file.FileName, ImageExtensions);
        var url = await storage.SaveAsync("avatars", userId, buffer, extension, ct);
        logger.LogInformation("Stored avatar upload {Url} for user {UserId}", url, userId);
        return url;
    }

    public async Task<string> SaveResumeAsync(Guid userId, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0)
            throw new ArgumentException("Le CV est vide.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        ValidateSize(buffer.Length, MaxResumeBytes);

        buffer.Position = 0;
        await moderation.EnsureDocumentAllowedAsync(
            buffer,
            file.ContentType ?? "application/octet-stream",
            file.FileName,
            ct);

        buffer.Position = 0;
        var extension = ValidateExtension(file.FileName, ResumeExtensions);
        var url = await storage.SaveAsync("resumes", userId, buffer, extension, ct);
        logger.LogInformation("Stored resume upload {Url} for user {UserId}", url, userId);
        return url;
    }

    private static void ValidateSize(long length, long maxBytes)
    {
        if (length == 0)
            throw new ArgumentException("Fichier vide.");

        if (length > maxBytes)
            throw new ArgumentException("Fichier trop volumineux.");
    }

    private static string ValidateExtension(string originalFileName, HashSet<string> allowedExtensions)
    {
        var extension = Path.GetExtension(originalFileName);
        if (!allowedExtensions.Contains(extension))
            throw new ArgumentException("Type de fichier non autorisé.");

        return extension;
    }
}
