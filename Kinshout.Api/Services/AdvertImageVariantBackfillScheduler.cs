using System.Text.Json;
using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IAdvertImageVariantBackfillScheduler
{
    void ScheduleBackfill();
}

/// <summary>Generates missing listing/display WebP variants for existing native uploads.</summary>
public sealed class AdvertImageVariantBackfillScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<AdvertImageVariantBackfillScheduler> logger) : IAdvertImageVariantBackfillScheduler
{
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
            const int batchSize = 15;
            const int maxAdverts = 150;
            var offset = 0;

            while (offset < maxAdverts)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
                var storage = scope.ServiceProvider.GetRequiredService<IUploadStorage>();
                var processor = scope.ServiceProvider.GetRequiredService<IAdvertImageProcessor>();

                var imageJsonRows = await db.Adverts
                    .AsNoTracking()
                    .Where(a => a.IsPublished && a.ImageUrlsJson != "[]")
                    .OrderBy(a => a.UpdatedAt)
                    .Skip(offset)
                    .Take(batchSize)
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

                        await EnsureVariantAsync(storage, processor, path, AdvertImageUrls.ThumbnailSuffix);
                        await EnsureVariantAsync(storage, processor, path, AdvertImageUrls.DisplaySuffix);
                    }
                }

                offset += imageJsonRows.Count;
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

    private static async Task EnsureVariantAsync(
        IUploadStorage storage,
        IAdvertImageProcessor processor,
        string originalPath,
        string suffix)
    {
        var variantPath = AdvertImageUrls.GetVariantPath(originalPath, suffix);
        if (variantPath is null || await storage.ExistsAsync(variantPath))
            return;

        var file = await storage.OpenReadAsync(originalPath);
        if (file is null)
            return;

        await using (file.Stream)
        {
            await using var variant = suffix == AdvertImageUrls.ThumbnailSuffix
                ? await processor.CreateListingThumbnailAsync(file.Stream)
                : await processor.CreateDisplayImageAsync(file.Stream);
            if (variant is null)
                return;

            var fileName = Path.GetFileName(variantPath);
            var userId = ExtractUserId(originalPath);
            if (userId is null)
                return;

            await storage.SaveNamedAsync("images", userId.Value, variant, fileName);
        }
    }

    private static Guid? ExtractUserId(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return null;

        return Guid.TryParseExact(parts[2], "N", out var id) ? id : null;
    }
}
