using System.Text.Json;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IExternalAdvertImageMirrorBackfillScheduler
{
    void ScheduleBackfill();
}

/// <summary>Re-mirrors hotlinked external advert images into Kinshout storage.</summary>
public sealed class ExternalAdvertImageMirrorBackfillScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<ExternalAdvertImageMirrorBackfillScheduler> logger) : IExternalAdvertImageMirrorBackfillScheduler
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
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
            var mirror = scope.ServiceProvider.GetRequiredService<IExternalAdvertImageMirrorService>();
            var importUser = await ImportSeed.EnsureImportUserAsync(db);

            var adverts = await db.Adverts
                .AsNoTracking()
                .Where(a => a.IsPublished
                    && a.SourceProvider != null
                    && a.SourceProvider != AdvertSourceProvider.Kinshout
                    && a.SourceExternalId != null
                    && a.ImageUrlsJson.Contains("http"))
                .OrderBy(a => a.UpdatedAt)
                .Take(25)
                .Select(a => new { a.Id, a.SourceProvider, a.SourceExternalId, a.ImageUrlsJson })
                .ToListAsync();

            foreach (var advert in adverts)
            {
                var urls = JsonSerializer.Deserialize<List<string>>(advert.ImageUrlsJson ?? "[]") ?? [];
                if (urls.Count == 0 || urls.All(u => AdvertImageUrls.NormalizeStoragePath(u) is not null))
                    continue;

                var mirrored = await mirror.MirrorAsync(
                    urls,
                    advert.SourceProvider!,
                    advert.SourceExternalId!,
                    importUser.Id);

                if (mirrored.Count == 0 || SequenceEqualUrls(urls, mirrored))
                    continue;

                var tracked = await db.Adverts.FirstOrDefaultAsync(a => a.Id == advert.Id);
                if (tracked is null)
                    continue;

                await mirror.DeleteMirroredAsync(urls);
                tracked.ImageUrlsJson = JsonSerializer.Serialize(mirrored);
                tracked.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External advert image mirror backfill failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static bool SequenceEqualUrls(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
