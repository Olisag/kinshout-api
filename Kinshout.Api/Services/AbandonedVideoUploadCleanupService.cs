namespace Kinshout.Api.Services;

/// <summary>
/// Hourly, deletes direct video uploads that were never completed: the web app uploads a video as
/// soon as it is picked and deletes it when the post is abandoned, but a closed tab cannot.
/// </summary>
public sealed class AbandonedVideoUploadCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<AbandonedVideoUploadCleanupService> logger) : BackgroundService
{
    // Leaves a day to publish a post whose video is already uploaded.
    private static readonly TimeSpan MaxUploadAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var videos = scope.ServiceProvider.GetRequiredService<IVideoService>();
                await videos.DeleteAbandonedUploadsAsync(MaxUploadAge, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not delete abandoned video uploads.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
