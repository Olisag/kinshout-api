using Kinshout.Api.Dtos;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public interface IUploadService
{
    Task<IReadOnlyList<string>> SaveImagesAsync(Guid userId, IFormFileCollection files, CancellationToken ct = default);
    Task<string> SaveResumeAsync(Guid userId, IFormFile file, CancellationToken ct = default);
}

public class UploadService(
    IWebHostEnvironment env,
    IAdvertModerationService moderation,
    ILogger<UploadService> logger) : IUploadService
{
    private const long MaxImageBytes = 5 * 1024 * 1024;
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
            buffer.Position = 0;

            await moderation.EnsureImageAllowedAsync(buffer, file.ContentType ?? "image/jpeg", ct);

            buffer.Position = 0;
            var url = await SaveStreamAsync(userId, buffer, file.FileName, ImageExtensions, MaxImageBytes, "images", ct);
            urls.Add(url);
        }

        return urls;
    }

    public async Task<string> SaveResumeAsync(Guid userId, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0)
            throw new ArgumentException("Le CV est vide.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return await SaveStreamAsync(userId, buffer, file.FileName, ResumeExtensions, MaxResumeBytes, "resumes", ct);
    }

    private async Task<string> SaveStreamAsync(
        Guid userId,
        Stream stream,
        string originalFileName,
        HashSet<string> allowedExtensions,
        long maxBytes,
        string folder,
        CancellationToken ct)
    {
        if (stream.Length == 0)
            throw new ArgumentException("Fichier vide.");

        if (stream.Length > maxBytes)
            throw new ArgumentException("Fichier trop volumineux.");

        var extension = Path.GetExtension(originalFileName);
        if (!allowedExtensions.Contains(extension))
            throw new ArgumentException("Type de fichier non autorisé.");

        var uploadsRoot = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "uploads", folder, userId.ToString("N"));
        Directory.CreateDirectory(uploadsRoot);

        var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var fullPath = Path.Combine(uploadsRoot, fileName);

        await using (var output = File.Create(fullPath))
            await stream.CopyToAsync(output, ct);

        logger.LogInformation("Saved upload {Path} for user {UserId}", fullPath, userId);
        return $"/uploads/{folder}/{userId:N}/{fileName}";
    }
}
