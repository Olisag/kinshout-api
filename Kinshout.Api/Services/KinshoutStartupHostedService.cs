using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

/// <summary>Runs seed/sync/backfill after the app starts accepting HTTP requests.</summary>
public sealed class KinshoutStartupHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<KinshoutStartupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RunAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();

            await DbSeed.SeedAsync(db);

            var clientSecret = configuration["ClientAuth:KinshoutWebSecret"];
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApiClient>>();
            await ClientSeed.EnsureClientSecretAsync(db, passwordHasher, clientSecret);
            await ClientSeed.EnsureAllowedOriginsAsync(db);
            await ImportSeed.EnsureImportUserAsync(db);

            var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
            await AiCategoryCatalog.SyncContentAsync(db, cache, ct);

            scope.ServiceProvider.GetRequiredService<IDiscussionTopicBackfillScheduler>().ScheduleBatchBackfill();
            scope.ServiceProvider.GetRequiredService<IAdvertImageVariantBackfillScheduler>().ScheduleBackfill();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background startup initialization failed.");
        }
    }
}
