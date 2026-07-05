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
        var minPublishedAt = now.AddDays(-Math.Max(1, settings.Schedule.MaxAdvertAgeDays));
        var importDtos = new List<ImportExternalDiscussionDto>();
        var removalDtos = new List<ImportExternalDiscussionDto>();
        var skipExisting = settings.Schedule.SkipExisting;
        var detectRemovals = settings.Schedule.DetectRemovedListings;

        IReadOnlySet<string>? knownKeys = null;
        if (skipExisting || detectRemovals)
        {
            try
            {
                var client = new KinshoutImportClient(http, settings.KinshoutApi);
                knownKeys = await client.FetchKnownDiscussionKeysAsync(ct);
                Console.WriteLine($"Loaded {knownKeys.Count} existing external discussion(s) from Kinshout.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load existing discussions ({ex.Message}); importing without skip/removal filters.");
                skipExisting = false;
                detectRemovals = false;
            }
        }

        foreach (var providerSettings in settings.DiscussionProviders.Where(p => p.Enabled))
        {
            if (string.IsNullOrWhiteSpace(providerSettings.Provider))
            {
                Console.WriteLine($"Skipping discussion provider '{providerSettings.Name}' because Provider is empty.");
                continue;
            }

            try
            {
                var provider = ExternalDiscussionProviderFactory.Create(http, providerSettings);
                var fetchResult = await provider.FetchAsync(ct);
                var sourceDiscussions = fetchResult.Discussions;

                if (detectRemovals && knownKeys is { Count: > 0 })
                {
                    var knownForProvider = ImportDiscussionKeys.KnownExternalIdsForProvider(knownKeys, providerSettings.Provider).ToList();
                    var removals = RemovedDiscussionDetector.BuildRemovals(
                        providerSettings,
                        fetchResult.SeenExternalIds,
                        knownKeys,
                        now);

                    if (removals.Count > 0)
                    {
                        removalDtos.AddRange(removals);
                        Console.WriteLine($"{provider.Name}: detected {removals.Count} removed discussion(s).");
                    }
                    else if (knownForProvider.Count > 0
                             && !RemovedDiscussionDetector.ShouldDetectRemovals(
                                 fetchResult.SeenExternalIds.Count,
                                 knownForProvider.Count))
                    {
                        Console.WriteLine(
                            $"{provider.Name}: skipped removal detection (seen {fetchResult.SeenExternalIds.Count} vs {knownForProvider.Count} known).");
                    }
                }

                var mappedBeforeFreshness = sourceDiscussions
                    .Select(d => DiscussionMapper.ToImportDto(d, providerSettings, now))
                    .Where(d => d is not null)
                    .Cast<ImportExternalDiscussionDto>()
                    .ToList();
                var mapped = mappedBeforeFreshness
                    .Where(d => IsFresh(d, minPublishedAt))
                    .ToList();
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

                importDtos.AddRange(mapped);
                Console.WriteLine(
                    $"{provider.Name}: fetched {sourceDiscussions.Count}, seen {fetchResult.SeenExternalIds.Count}, mapped {mapped.Count}, skipped_old={skippedOld}, skipped_existing={skippedExisting}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{providerSettings.Name}: failed: {ex.Message}");
            }
        }

        var deduped = importDtos
            .GroupBy(d => $"{d.Source.Provider}:{d.Source.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var dedupedRemovals = removalDtos
            .GroupBy(d => $"{d.Source.Provider}:{d.Source.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var payload = deduped
            .Concat(dedupedRemovals)
            .GroupBy(d => $"{d.Source.Provider}:{d.Source.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(d => d.Status is "removed" or "inactive") ?? g.First())
            .ToList();

        Console.WriteLine(
            $"Total mapped discussions: {importDtos.Count}; after dedupe: {deduped.Count}; removals: {dedupedRemovals.Count}; payload: {payload.Count}.");

        if (dryRun)
        {
            Console.WriteLine("Dry run enabled; not posting discussions to Kinshout.");
            return;
        }

        if (payload.Count == 0)
        {
            Console.WriteLine("Nothing to import.");
            return;
        }

        var importClient = new KinshoutImportClient(http, settings.KinshoutApi);
        var batchSize = Math.Max(1, settings.KinshoutApi.BatchSize);
        var totals = new ImportExternalDiscussionsResponseDto(0, 0, 0, 0);

        foreach (var batch in payload.Chunk(batchSize))
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

        Console.WriteLine(
            $"Discussion import complete. created={totals.Created}, updated={totals.Updated}, unchanged={totals.Unchanged}, skipped={totals.Skipped}");
    }

    private static bool IsFresh(ImportExternalDiscussionDto discussion, DateTime minPublishedAt) =>
        discussion.PublishedAt is null || discussion.PublishedAt.Value.ToUniversalTime() >= minPublishedAt;
}
