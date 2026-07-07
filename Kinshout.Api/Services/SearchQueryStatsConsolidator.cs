using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Kinshout.Api.Services;

/// <summary>
/// Merges historical popular-search rows that differ only by spelling, word order, or stop words.
/// Idempotent: safe to run on every startup; no-ops once rows are consolidated.
/// </summary>
public static class SearchQueryStatsConsolidator
{
    public static async Task ConsolidateHistoricalDuplicatesAsync(
        KinshoutDbContext db,
        IMemoryCache cache,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var rows = await db.SearchQueryStats.ToListAsync(ct);
        if (rows.Count == 0)
            return;

        var groups = new Dictionary<string, List<Models.SearchQueryStat>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = SearchQueryHelper.ResolveStatKey(row);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add(row);
        }

        var toRemove = new List<Models.SearchQueryStat>();
        var updates = new List<(Models.SearchQueryStat Keeper, string Key, int Count)>();

        foreach (var (key, group) in groups)
        {
            var keeper = group
                .OrderByDescending(r => r.LastSearchedAt)
                .ThenByDescending(r => r.SearchCount)
                .First();

            var mergedCount = group.Sum(r => r.SearchCount);
            var needsUpdate = group.Count > 1
                || !string.Equals(keeper.NormalizedQuery, key, StringComparison.Ordinal)
                || keeper.SearchCount != mergedCount;

            if (!needsUpdate)
                continue;

            toRemove.AddRange(group.Where(r => r.Id != keeper.Id));
            updates.Add((keeper, key, mergedCount));
        }

        if (toRemove.Count == 0 && updates.Count == 0)
            return;

        try
        {
            if (toRemove.Count > 0)
            {
                db.SearchQueryStats.RemoveRange(toRemove);
                await db.SaveChangesAsync(ct);
            }

            foreach (var (keeper, key, count) in updates)
            {
                keeper.NormalizedQuery = key;
                keeper.SearchCount = count;
            }

            await db.SaveChangesAsync(ct);
            cache.Remove(ApiCacheKeys.PopularSearches);
            logger?.LogInformation(
                "Consolidated popular search stats: removed {Removed}, updated {Updated}.",
                toRemove.Count,
                updates.Count);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to consolidate popular search stats.");
            throw;
        }
    }
}
