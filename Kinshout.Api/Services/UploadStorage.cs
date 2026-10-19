using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record UploadFileContent(Stream Stream, string ContentType);

public interface IUploadStorage
{
    Task<string> SaveAsync(string folder, Guid userId, Stream stream, string extension, CancellationToken ct = default);
    Task<string> SaveNamedAsync(string folder, Guid userId, Stream stream, string fileName, CancellationToken ct = default);
    Task<UploadFileContent?> OpenReadAsync(string uploadUrl, CancellationToken ct = default);
    Task DeleteIfExistsAsync(string uploadUrl, CancellationToken ct = default);
    Task<bool> ExistsAsync(string uploadUrl, CancellationToken ct = default);

    /// <summary>
    /// Short-lived read URL that clients can fetch straight from storage (range requests included),
    /// or null when the storage cannot hand one out and the API has to stream the file itself.
    /// </summary>
    Task<Uri?> GetDirectReadUriAsync(string uploadUrl, CancellationToken ct = default) =>
        Task.FromResult<Uri?>(null);

    /// <summary>
    /// Short-lived URL clients can upload a file to (in blocks) without going through the API,
    /// or null when the storage cannot hand one out.
    /// </summary>
    Task<Uri?> GetDirectWriteUriAsync(string uploadUrl, TimeSpan lifetime, CancellationToken ct = default) =>
        Task.FromResult<Uri?>(null);

    /// <summary>Size in bytes of a stored file, or null when it does not exist.</summary>
    Task<long?> GetLengthAsync(string uploadUrl, CancellationToken ct = default) =>
        Task.FromResult<long?>(null);
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
        var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        return await SaveNamedAsync(folder, userId, stream, fileName, ct);
    }

    public async Task<string> SaveNamedAsync(
        string folder,
        Guid userId,
        Stream stream,
        string fileName,
        CancellationToken ct = default)
    {
        var uploadsRoot = GetUploadsRoot();
        var userFolder = Path.Combine(uploadsRoot, folder, userId.ToString("N"));
        Directory.CreateDirectory(userFolder);

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

    public Task<long?> GetLengthAsync(string uploadUrl, CancellationToken ct = default) =>
        Task.FromResult<long?>(
            TryResolvePhysicalPath(uploadUrl, out var fullPath) && File.Exists(fullPath)
                ? new FileInfo(fullPath).Length
                : null);

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
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
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
    /// <summary>Direct read URLs stay valid for one to two windows.</summary>
    public static readonly TimeSpan DirectReadUriWindow = TimeSpan.FromHours(6);

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
        return await SaveNamedAsync(folder, userId, stream, fileName, ct);
    }

    public async Task<string> SaveNamedAsync(
        string folder,
        Guid userId,
        Stream stream,
        string fileName,
        CancellationToken ct = default)
    {
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

        BlobProperties properties;
        try
        {
            properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var contentType = properties.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = LocalUploadStorage.GetContentTypeFromPath(blobName);

        // Must be seekable with a known length: ASP.NET silently ignores Range requests otherwise,
        // and Safari refuses to play video that isn't served as 206 Partial Content.
        var stream = await blob.OpenReadAsync(
            new BlobOpenReadOptions(allowModifications: false) { BufferSize = 1024 * 1024 },
            ct);

        return new UploadFileContent(stream, contentType);
    }

    public async Task<Uri?> GetDirectReadUriAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (!TryGetBlobName(uploadUrl, out var blobName))
            return null;

        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(blobName);
        if (!blob.CanGenerateSasUri)
            return null;

        // Expiry snapped to a fixed window so every viewer gets the same URL for hours:
        // browsers can then reuse cached ranges instead of refetching the video.
        var windowTicks = DirectReadUriWindow.Ticks;
        var windowStart = new DateTimeOffset(
            DateTimeOffset.UtcNow.UtcTicks / windowTicks * windowTicks,
            TimeSpan.Zero);
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, windowStart + DirectReadUriWindow * 2)
        {
            BlobContainerName = container.Name,
            BlobName = blobName,
            Resource = "b",
            CacheControl = "public,max-age=31536000,immutable",
        };

        return blob.GenerateSasUri(sas);
    }

    public async Task<Uri?> GetDirectWriteUriAsync(
        string uploadUrl,
        TimeSpan lifetime,
        CancellationToken ct = default)
    {
        if (!TryGetBlobName(uploadUrl, out var blobName))
            return null;

        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(blobName);
        if (!blob.CanGenerateSasUri)
            return null;

        var sas = new BlobSasBuilder(
            BlobSasPermissions.Create | BlobSasPermissions.Write,
            DateTimeOffset.UtcNow + lifetime)
        {
            BlobContainerName = container.Name,
            BlobName = blobName,
            Resource = "b",
        };

        return blob.GenerateSasUri(sas);
    }

    public async Task<long?> GetLengthAsync(string uploadUrl, CancellationToken ct = default)
    {
        if (!TryGetBlobName(uploadUrl, out var blobName))
            return null;

        var container = await GetContainerAsync(ct);
        try
        {
            var properties = await container.GetBlobClient(blobName).GetPropertiesAsync(cancellationToken: ct);
            return properties.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
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
