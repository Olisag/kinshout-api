using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IImportedDiscussionImageModerationBackfillScheduler
{
    void ScheduleBackfill();
}

/// <summary>
/// One-time (per media rule version) vision pass over existing discussions with images.
/// New imports are moderated inline; this catches older rows that never were.
/// </summary>
public sealed class ImportedDiscussionImageModerationBackfillScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<ImportedDiscussionImageModerationBackfillScheduler> logger)
    : IImportedDiscussionImageModerationBackfillScheduler
{
    private const int BatchSize = 8;
    private int _running;

    public void ScheduleBackfill()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var reviewed = 0;
        var blocked = 0;

        try
        {
            while (true)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
                var moderation = scope.ServiceProvider.GetRequiredService<IAdvertModerationService>();
                var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

                var batch = await db.Discussions
                    .Where(d =>
                        d.PublicFeedMediaRuleVersion < ImportedDiscussionImageModeration.RuleVersion
                        && d.ImageUrlsJson != "[]"
                        && d.ImageUrlsJson != null
                        && d.ImageUrlsJson != "")
                    .OrderBy(d => d.Id)
                    .Take(BatchSize)
                    .ToListAsync();

                if (batch.Count == 0)
                    break;

                foreach (var discussion in batch)
                {
                    var wasBlocked = discussion.PublicFeedBlocked;
                    await ImportedDiscussionImageModeration.ApplyAsync(
                        discussion,
                        httpClientFactory,
                        moderation,
                        logger,
                        CancellationToken.None);
                    if (!wasBlocked && discussion.PublicFeedBlocked)
                        blocked++;
                    reviewed++;
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }

            if (reviewed > 0)
            {
                logger.LogInformation(
                    "Imported discussion image moderation backfill reviewed {Reviewed} posts; {Blocked} newly hidden.",
                    reviewed,
                    blocked);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Imported discussion image moderation backfill failed after reviewing {Reviewed} posts.",
                reviewed);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
