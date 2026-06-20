using Kinshout.Api.Data;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServicePopularTests
{
    [Fact]
    public async Task RecordSearchQueryAsync_IncrementsCountForSameQuery()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await service.RecordSearchQueryAsync("Appartement à Gombe");
        await service.RecordSearchQueryAsync("  appartement   à gombe  ");

        var popular = await service.GetPopularSearchesAsync();
        Assert.Single(popular);
        Assert.Equal("appartement à gombe", popular[0].Query.ToLowerInvariant());
        Assert.Equal(2, popular[0].Count);
    }

    [Fact]
    public async Task GetPopularSearchesAsync_ReturnsTopTenOrderedByCount()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        for (var i = 0; i < 12; i++)
            await service.RecordSearchQueryAsync($"Query {i}");

        for (var i = 0; i < 5; i++)
            await service.RecordSearchQueryAsync("Query 11");

        var popular = await service.GetPopularSearchesAsync(10);
        Assert.Equal(10, popular.Count);
        Assert.Equal("Query 11", popular[0].Query);
        Assert.Equal(6, popular[0].Count);
    }

    [Fact]
    public async Task RecordSearchQueryAsync_IgnoresTooShortQueries()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await service.RecordSearchQueryAsync("a");

        var popular = await service.GetPopularSearchesAsync();
        Assert.Empty(popular);
    }

    private static SearchService CreateService(KinshoutDbContext db)
    {
        var openAi = new Mock<IOpenAiService>();
        return new SearchService(db, openAi.Object);
    }
}
