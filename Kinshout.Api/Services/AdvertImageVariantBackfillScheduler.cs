using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IAdvertImageVariantBackfillScheduler
{
    void ScheduleBackfill();
}

/// <summary>Generates missing listing thumbnail WebP variants for existing native Kinshout uploads.</summary>
public sealed class AdvertImageVariantBackfillScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<AdvertImageVariantBackfillScheduler> logger) : IAdvertImageVariantBackfillScheduler
{
    private const int BatchSize = 25;
    private int _running;

    public void ScheduleBackfill()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var offset = 0;
            var thumbnailsCreated = 0;
            var displaysCreated = 0;

            while (true)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
                var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();
                var processor = scope.ServiceProvider.GetRequiredService<IAdvertImageProcessor>();

                var imageJsonRows = await db.Adverts
                    .AsNoTracking()
                    .Where(a => a.IsPublished
                        && a.ImageUrlsJson.Contains("/uploads/images/")
                        && (a.SourceProvider == null || a.SourceProvider == AdvertSourceProvider.Kinshout))
                    .OrderBy(a => a.UpdatedAt)
                    .Skip(offset)
                    .Take(BatchSize)
                    .Select(a => a.ImageUrlsJson)
                    .ToListAsync();

                if (imageJsonRows.Count == 0)
                    break;

                foreach (var json in imageJsonRows)
                {
                    var urls = JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];
                    foreach (var url in urls)
                    {
                        var path = AdvertImageUrls.NormalizeStoragePath(url);
                        if (path is null)
                            continue;

                        if (await EnsureVariantAsync(storage, processor, path, AdvertImageUrls.ThumbnailSuffix))
                            thumbnailsCreated++;

                        if (await EnsureVariantAsync(storage, processor, path, AdvertImageUrls.DisplaySuffix))
                            displaysCreated++;
                    }
                }

                offset += imageJsonRows.Count;
            }

            if (thumbnailsCreated > 0 || displaysCreated > 0)
            {
                logger.LogInformation(
                    "Native advert image backfill created {ThumbnailCount} thumbnails and {DisplayCount} display variants.",
                    thumbnailsCreated,
                    displaysCreated);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Advert image variant backfill failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    internal static async Task<bool> EnsureVariantAsync(
        IUploadStorage storage,
        IAdvertImageProcessor processor,
        string originalPath,
        string suffix)
    {
        var variantPath = AdvertImageUrls.GetVariantPath(originalPath, suffix);
        if (variantPath is null || await storage.ExistsAsync(variantPath))
            return false;

        var file = await storage.OpenReadAsync(originalPath);
        if (file is null)
            return false;

        await using (file.Stream)
        {
            await using var variant = suffix == AdvertImageUrls.ThumbnailSuffix
                ? await processor.CreateListingThumbnailAsync(file.Stream)
                : await processor.CreateDisplayImageAsync(file.Stream);
            if (variant is null)
                return false;

            var fileName = Path.GetFileName(variantPath);
            var userId = ExtractUserId(originalPath);
            if (userId is null)
                return false;

            await storage.SaveNamedAsync("images", userId.Value, variant, fileName);
            return true;
        }
    }

    internal static Guid? ExtractUserId(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return null;

        return Guid.TryParseExact(parts[2], "N", out var id) ? id : null;
    }
}
