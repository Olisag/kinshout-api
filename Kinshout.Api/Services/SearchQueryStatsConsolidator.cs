using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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
        CancellationToken ct = default)
    {
        var rows = await db.SearchQueryStats.ToListAsync(ct);
        if (rows.Count == 0)
            return;

        var groups = new Dictionary<string, List<Models.SearchQueryStat>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = SearchQueryHelper.CanonicalKey(row.DisplayQuery) ?? row.NormalizedQuery;
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }

            group.Add(row);
        }

        var changed = false;
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

            foreach (var duplicate in group.Where(r => r.Id != keeper.Id))
            {
                db.SearchQueryStats.Remove(duplicate);
                changed = true;
            }

            keeper.NormalizedQuery = key;
            keeper.SearchCount = mergedCount;
            changed = true;
        }

        if (!changed)
            return;

        await db.SaveChangesAsync(ct);
        cache.Remove(ApiCacheKeys.PopularSearches);
    }
}
