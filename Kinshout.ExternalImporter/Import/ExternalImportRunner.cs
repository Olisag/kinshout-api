using Kinshout.ExternalImporter.Configuration;
using Kinshout.ExternalImporter.Providers;

namespace Kinshout.ExternalImporter.Import;

public sealed class ExternalImportRunner(
    HttpClient http,
    ImporterSettings settings,
    bool dryRun)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var minPublishedAt = now.AddDays(-Math.Max(1, settings.Schedule.MaxAdvertAgeDays));
        var importDtos = new List<ImportExternalAdvertDto>();
        var skipExisting = settings.Schedule.SkipExisting;

        IReadOnlySet<string>? knownKeys = null;
        if (skipExisting)
        {
            try
            {
                var client = new KinshoutImportClient(http, settings.KinshoutApi);
                knownKeys = await client.FetchKnownAdvertKeysAsync(ct);
                Console.WriteLine($"Loaded {knownKeys.Count} existing external advert(s) from Kinshout.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load existing adverts ({ex.Message}); importing without skip filter.");
                skipExisting = false;
            }
        }

        foreach (var providerSettings in settings.Providers.Where(p => p.Enabled))
        {
            if (string.IsNullOrWhiteSpace(providerSettings.Provider))
            {
                Console.WriteLine($"Skipping provider '{providerSettings.Name}' because Provider is empty.");
                continue;
            }

            try
            {
                providerSettings.KnownAdvertKeys = knownKeys;
                var provider = ExternalAdvertProviderFactory.Create(http, providerSettings);
                var sourceAdverts = await provider.FetchAsync(ct);
                var mappedBeforeFreshness = sourceAdverts
                    .Select(ad => AdvertMapper.ToImportDto(ad, providerSettings, now))
                    .Where(ad => ad is not null)
                    .Cast<ImportExternalAdvertDto>()
                    .ToList();
                var mapped = mappedBeforeFreshness
                    .Where(ad => IsFresh(ad, minPublishedAt))
                    .ToList();
                var skippedOld = mappedBeforeFreshness.Count - mapped.Count;

                var skippedExisting = 0;
                if (skipExisting && knownKeys is { Count: > 0 })
                {
                    var before = mapped.Count;
                    mapped = mapped
                        .Where(ad => !ImportAdvertKeys.IsKnown(knownKeys, ad.Source.Provider, ad.Source.ExternalId))
                        .ToList();
                    skippedExisting = before - mapped.Count;
                }

                importDtos.AddRange(mapped);
                Console.WriteLine(
                    $"{provider.Name}: fetched {sourceAdverts.Count}, mapped {mapped.Count}, skipped_old={skippedOld}, skipped_existing={skippedExisting}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{providerSettings.Name}: failed: {ex.Message}");
            }
        }

        var deduped = importDtos
            .GroupBy(ad => $"{ad.Source.Provider}:{ad.Source.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Console.WriteLine($"Total mapped adverts: {importDtos.Count}; after dedupe: {deduped.Count}.");

        if (dryRun)
        {
            Console.WriteLine("Dry run enabled; not posting to Kinshout.");
            return;
        }

        if (deduped.Count == 0)
        {
            Console.WriteLine("Nothing new to import.");
            return;
        }

        var importClient = new KinshoutImportClient(http, settings.KinshoutApi);
        var batchSize = Math.Max(1, settings.KinshoutApi.BatchSize);
        var totals = new ImportExternalAdvertsResponseDto(0, 0, 0, 0);

        foreach (var batch in deduped.Chunk(batchSize))
        {
            var response = await importClient.ImportAsync(batch, ct);
            totals = totals with
            {
                Created = totals.Created + response.Created,
                Updated = totals.Updated + response.Updated,
                Unchanged = totals.Unchanged + response.Unchanged,
                Skipped = totals.Skipped + response.Skipped,
            };
        }

        Console.WriteLine(
            $"Import complete. created={totals.Created}, updated={totals.Updated}, unchanged={totals.Unchanged}, skipped={totals.Skipped}");
    }

    private static bool IsFresh(ImportExternalAdvertDto advert, DateTime minPublishedAt) =>
        advert.PublishedAt is null || advert.PublishedAt.Value.ToUniversalTime() >= minPublishedAt;
}
