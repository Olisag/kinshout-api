using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Tests;

public class SearchQueryStatsConsolidatorTests
{
    [Fact]
    public async Task ConsolidateHistoricalDuplicatesAsync_MergesSpellingVariants()
    {
        await using var db = TestDbFactory.Create();
        db.SearchQueryStats.AddRange(
            new SearchQueryStat
            {
                NormalizedQuery = "apartment gombe",
                DisplayQuery = "Je cherche un apartment à Gombe",
                SearchCount = 3,
                LastSearchedAt = DateTime.UtcNow.AddHours(-2),
            },
            new SearchQueryStat
            {
                NormalizedQuery = "appartement gombe",
                DisplayQuery = "Je cherche un appartement à Gombe",
                SearchCount = 5,
                LastSearchedAt = DateTime.UtcNow.AddHours(-1),
            });
        await db.SaveChangesAsync();

        await SearchQueryStatsConsolidator.ConsolidateHistoricalDuplicatesAsync(
            db,
            TestDbFactory.CreateMemoryCache());

        var rows = await db.SearchQueryStats.ToListAsync();
        Assert.Single(rows);
        Assert.Equal("appartement gombe", rows[0].NormalizedQuery);
        Assert.Equal(8, rows[0].SearchCount);
        Assert.Equal("Je cherche un appartement à Gombe", rows[0].DisplayQuery);
    }

    [Fact]
    public async Task ConsolidateHistoricalDuplicatesAsync_IsIdempotent()
    {
        await using var db = TestDbFactory.Create();
        db.SearchQueryStats.Add(new SearchQueryStat
        {
            NormalizedQuery = "apartment gombe",
            DisplayQuery = "Je cherche un apartment à Gombe",
            SearchCount = 2,
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        await SearchQueryStatsConsolidator.ConsolidateHistoricalDuplicatesAsync(db, cache);
        await SearchQueryStatsConsolidator.ConsolidateHistoricalDuplicatesAsync(db, cache);

        var rows = await db.SearchQueryStats.ToListAsync();
        Assert.Single(rows);
        Assert.Equal("appartement gombe", rows[0].NormalizedQuery);
        Assert.Equal(2, rows[0].SearchCount);
    }

    [Fact]
    public async Task ConsolidateHistoricalDuplicatesAsync_UpdatesStaleNormalizedKeyWithoutDuplicates()
    {
        await using var db = TestDbFactory.Create();
        db.SearchQueryStats.Add(new SearchQueryStat
        {
            NormalizedQuery = "apartment gombe",
            DisplayQuery = "Je cherche un apartment à Gombe",
            SearchCount = 4,
        });
        await db.SaveChangesAsync();

        await SearchQueryStatsConsolidator.ConsolidateHistoricalDuplicatesAsync(
            db,
            TestDbFactory.CreateMemoryCache());

        var row = Assert.Single(await db.SearchQueryStats.ToListAsync());
        Assert.Equal("appartement gombe", row.NormalizedQuery);
        Assert.Equal(4, row.SearchCount);
    }
}
