using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services.WhatsApp;

public sealed class WhatsAppPublishingWorker(
    IServiceScopeFactory scopes,
    IOptions<WhatsAppPublishingSettings> settings,
    ILogger<WhatsAppPublishingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled)
            logger.LogInformation("WhatsApp channel publishing is off. The scheduler will not send messages.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.Value.Enabled)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var engine = scope.ServiceProvider.GetRequiredService<WhatsAppPublishingEngine>();
                    await engine.TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "WhatsApp publishing tick failed. The next tick will try again.");
                }
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
