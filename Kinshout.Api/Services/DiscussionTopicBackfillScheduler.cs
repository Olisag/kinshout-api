namespace Kinshout.Api.Services;

public interface IDiscussionTopicBackfillScheduler
{
    void ScheduleBatchBackfill();
}

/// <summary>Runs keyword-based topic backfill off the request thread.</summary>
public sealed class DiscussionTopicBackfillScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<DiscussionTopicBackfillScheduler> logger) : IDiscussionTopicBackfillScheduler
{
    private int _running;

    public void ScheduleBatchBackfill()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var processed = 0;
            do
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Data.KinshoutDbContext>();
                var cache = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                processed = await AiDiscussionCategoryCatalog.BackfillUncategorizedAsync(db, cache);
            }
            while (processed > 0);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discussion topic backfill failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
