using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record UploadFileContent(Stream Stream, string ContentType);

public interface IUploadStorage
{
    Task<string> SaveAsync(string folder, Guid userId, Stream stream, string extension, CancellationToken ct = default);
    Task<UploadFileContent?> OpenReadAsync(string uploadUrl, CancellationToken ct = default);
    Task DeleteIfExistsAsync(string uploadUrl, CancellationToken ct = default);
    Task<bool> ExistsAsync(string uploadUrl, CancellationToken ct = default);
}

public sealed class LocalUploadStorage(IWebHostEnvironment env, ILogger<LocalUploadStorage> logger) : IUploadStorage
{
    public async Task<string> SaveAsync(
        string folder,
        Guid userId,
        Stream stream,
        string extension,
        CancellationToken ct = default)
    {
        var uploadsRoot = GetUploadsRoot();
        var userFolder = Path.Combine(uploadsRoot, folder, userId.ToString("N"));
        Directory.CreateDirectory(userFolder);

        var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var fullPath = Path.Combine(userFolder, fileName);

        await using (var output = File.Create(fullPath))
            await stream.CopyToAsync(output, ct);

        logger.LogInformation("Saved local upload {Path}", fullPath);
        return BuildUrl(folder, userId, fileName);
    }

    public Task<UploadFileContent?> OpenReadAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (!TryResolvePhysicalPath(uploadUrl, out var fullPath) || !File.Exists(fullPath))
            return Task.FromResult<UploadFileContent?>(null);

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult<UploadFileContent?>(new UploadFileContent(stream, GetContentTypeFromPath(fullPath)));
    }

    public Task DeleteIfExistsAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (TryResolvePhysicalPath(uploadUrl, out var fullPath) && File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string uploadUrl, CancellationToken ct = default) =>
        Task.FromResult(TryResolvePhysicalPath(uploadUrl, out var fullPath) && File.Exists(fullPath));

    private string GetUploadsRoot() =>
        Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "uploads");

    private bool TryResolvePhysicalPath(string uploadUrl, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryParseUploadUrl(uploadUrl, out var relativePath))
            return false;

        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
        var uploadsRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads"));
        return fullPath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildUrl(string folder, Guid userId, string fileName) =>
        $"/uploads/{folder}/{userId:N}/{fileName}";

    internal static bool TryParseUploadUrl(string uploadUrl, out string relativePath)
    {
        relativePath = string.Empty;
        if (!uploadUrl.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return false;

        relativePath = uploadUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return true;
    }

    internal static string GetContentTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "image/jpeg",
        };
}

public sealed class AzureBlobUploadStorage(
    IOptions<UploadStorageSettings> options,
    ILogger<AzureBlobUploadStorage> logger) : IUploadStorage
{
    private readonly UploadStorageSettings _settings = options.Value;
    private BlobContainerClient? _container;

    public async Task<string> SaveAsync(
        string folder,
        Guid userId,
        Stream stream,
        string extension,
        CancellationToken ct = default)
    {
        var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var blobName = BuildBlobName(folder, userId, fileName);
        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(blobName);

        await blob.UploadAsync(stream, new BlobHttpHeaders
        {
            ContentType = LocalUploadStorage.GetContentTypeFromPath(fileName),
        }, cancellationToken: ct);

        logger.LogInformation("Saved blob upload {BlobName}", blobName);
        return LocalUploadStorage.BuildUrl(folder, userId, fileName);
    }

    public async Task<UploadFileContent?> OpenReadAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (!TryGetBlobName(uploadUrl, out var blobName))
            return null;

        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(blobName);
        if (!await blob.ExistsAsync(ct))
            return null;

        var download = await blob.DownloadStreamingAsync(cancellationToken: ct);
        var contentType = download.Value.Details.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = LocalUploadStorage.GetContentTypeFromPath(blobName);

        return new UploadFileContent(download.Value.Content, contentType);
    }

    public async Task DeleteIfExistsAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (!TryGetBlobName(uploadUrl, out var blobName))
            return;

        var container = await GetContainerAsync(ct);
        await container.DeleteBlobIfExistsAsync(blobName, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (!TryGetBlobName(uploadUrl, out var blobName))
            return false;

        var container = await GetContainerAsync(ct);
        return await container.GetBlobClient(blobName).ExistsAsync(ct);
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        if (_container is not null)
            return _container;

        var service = new BlobServiceClient(_settings.AzureBlobConnectionString);
        _container = service.GetBlobContainerClient(_settings.ContainerName);
        await _container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);
        return _container;
    }

    private static string BuildBlobName(string folder, Guid userId, string fileName) =>
        $"{folder}/{userId:N}/{fileName}";

    private static bool TryGetBlobName(string uploadUrl, out string blobName)
    {
        blobName = string.Empty;
        if (!LocalUploadStorage.TryParseUploadUrl(uploadUrl, out var relativePath))
            return false;

        blobName = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        if (!blobName.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
            return false;

        blobName = blobName["uploads/".Length..];
        return !string.IsNullOrWhiteSpace(blobName);
    }
}
