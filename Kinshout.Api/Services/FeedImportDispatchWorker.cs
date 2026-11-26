using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public sealed class FeedImportDispatchWorker(
    IServiceScopeFactory scopes,
    IOptions<FeedImportDispatchSettings> settings,
    ILogger<FeedImportDispatchWorker> logger) : BackgroundService
{
    private bool _reportedMissingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = settings.Value;
                if (config.Enabled && string.IsNullOrWhiteSpace(config.GitHubToken) && !_reportedMissingToken)
                {
                    logger.LogWarning("The feed import clock is on, and it is waiting for a GitHub token before it can start a workflow.");
                    _reportedMissingToken = true;
                }
                else if (config.Enabled && !string.IsNullOrWhiteSpace(config.GitHubToken))
                {
                    using var scope = scopes.CreateScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<FeedImportDispatchService>();
                    await dispatcher.DispatchDueAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Feed import clock tick failed. The next tick will try again.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.Value.TickSeconds, 30, 300)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
