using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers;

namespace Kinshout.ExternalImporter.Import;

public sealed class ExternalDiscussionImportRunner(
    HttpClient http,
    ImporterSettings settings,
    bool dryRun)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var maxDiscussionAgeDays = Math.Max(1, settings.Schedule.MaxDiscussionAgeDays);
        var skipExisting = settings.Schedule.SkipExisting;
        var importClient = new KinshoutImportClient(http, settings.KinshoutApi);
        try
        {
            var remoteSources = await importClient.FetchSourceRegistryAsync(ct);
            SourceRegistry.UseRemote(remoteSources);
            Console.WriteLine($"Loaded {remoteSources.Count} registry source(s) from Kinshout.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load the registry from Kinshout ({ex.Message}); using the local file.");
        }
        var totals = new ImportExternalDiscussionsResponseDto(0, 0, 0, 0);

        IReadOnlySet<string>? knownKeys = null;
        IReadOnlyDictionary<string, DateTime> lastRunByProvider =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        try
        {
            lastRunByProvider = await importClient.FetchDiscussionImportStateAsync(ct);
            Console.WriteLine($"Loaded discussion import state for {lastRunByProvider.Count} provider(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load discussion import state ({ex.Message}); using {maxDiscussionAgeDays}-day fallback window.");
        }

        if (skipExisting)
        {
            try
            {
                knownKeys = await importClient.FetchKnownDiscussionKeysAsync(ct);
                Console.WriteLine($"Loaded {knownKeys.Count} existing external discussion(s) from Kinshout.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load existing discussions ({ex.Message}); importing without skip filter.");
                skipExisting = false;
            }
        }

        var providers = settings.DiscussionProviders.Where(p => p.Enabled);
        if (settings.Schedule.ProviderFilter.Count > 0)
        {
            providers = providers.Where(p =>
                settings.Schedule.ProviderFilter.Contains(p.Provider)
                || settings.Schedule.ProviderFilter.Contains(p.Type));
        }

        if (settings.Schedule.ClassiquesBackfill || settings.Schedule.YouTubeBuzz)
            providers = providers.Where(p => p.Type.Contains("youtube-data-api", StringComparison.OrdinalIgnoreCase));

        foreach (var providerSettings in providers)
        {
            providerSettings.ClassiquesBackfill = settings.Schedule.ClassiquesBackfill;
            providerSettings.YouTubeBuzz = settings.Schedule.YouTubeBuzz;
            if (string.IsNullOrWhiteSpace(providerSettings.Provider))
            {
                Console.WriteLine($"Skipping discussion provider '{providerSettings.Name}' because Provider is empty.");
                continue;
            }

            var sinceUtc = DiscussionImportWindow.ResolveSinceUtc(
                providerSettings.Provider,
                lastRunByProvider,
                now,
                maxDiscussionAgeDays);
            providerSettings.SincePublishedAt = sinceUtc;
            Console.WriteLine(
                $"{providerSettings.Name}: importing discussions published since {sinceUtc:yyyy-MM-dd HH:mm} UTC.");

            try
            {
                var provider = ExternalDiscussionProviderFactory.Create(http, providerSettings);
                var fetchResult = await provider.FetchAsync(ct);

                var mappedBeforeFreshness = fetchResult.Discussions
                    .Select(d => DiscussionMapper.ToImportDto(d, providerSettings, now))
                    .Where(d => d is not null)
                    .Cast<ImportExternalDiscussionDto>()
                    .ToList();
                var mapped = settings.Schedule.ClassiquesBackfill
                    ? mappedBeforeFreshness
                    : mappedBeforeFreshness.Where(d => IsSinceLastImport(d, sinceUtc)).ToList();
                var skippedOld = mappedBeforeFreshness.Count - mapped.Count;

                var skippedExisting = 0;
                if (skipExisting && knownKeys is { Count: > 0 })
                {
                    var before = mapped.Count;
                    mapped = mapped
                        .Where(d => !ImportDiscussionKeys.IsKnown(knownKeys, d.Source.Provider, d.Source.ExternalId))
                        .ToList();
                    skippedExisting = before - mapped.Count;
                }

                var deduped = mapped
                    .GroupBy(d => $"{d.Source.Provider}:{d.Source.ExternalId}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                Console.WriteLine(
                    $"{provider.Name}: fetched {fetchResult.Discussions.Count}, seen {fetchResult.SeenExternalIds.Count}, mapped {deduped.Count}, skipped_old={skippedOld}, skipped_existing={skippedExisting}.");

                if (dryRun)
                    continue;

                if (deduped.Count > 0)
                {
                    var batchSize = Math.Max(1, settings.KinshoutApi.BatchSize);
                    foreach (var batch in deduped.Chunk(batchSize))
                    {
                        var response = await importClient.ImportDiscussionsAsync(batch, ct);
                        totals = totals with
                        {
                            Created = totals.Created + response.Created,
                            Updated = totals.Updated + response.Updated,
                            Unchanged = totals.Unchanged + response.Unchanged,
                            Skipped = totals.Skipped + response.Skipped,
                        };
                    }
                }

                await importClient.RecordDiscussionImportRunAsync(providerSettings.Provider, now, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{providerSettings.Name}: failed: {ex.Message}");
            }
        }

        if (dryRun)
        {
            Console.WriteLine("Dry run enabled; not posting discussions to Kinshout.");
            return;
        }

        Console.WriteLine(
            $"Discussion import complete. created={totals.Created}, updated={totals.Updated}, unchanged={totals.Unchanged}, skipped={totals.Skipped}");
    }

    private static bool IsSinceLastImport(ImportExternalDiscussionDto discussion, DateTime sinceUtc) =>
        discussion.PublishedAt is null || discussion.PublishedAt.Value.ToUniversalTime() >= sinceUtc;
}
