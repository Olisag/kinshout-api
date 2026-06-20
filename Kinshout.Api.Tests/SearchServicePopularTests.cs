using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServicePopularTests
{
    [Fact]
    public async Task SearchAsync_IncrementsPopularCountForSameQuery()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await service.SearchAsync(new SearchRequestDto("Appartement à Gombe"));
        await service.SearchAsync(new SearchRequestDto("  appartement   à gombe  "));

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
            await service.SearchAsync(new SearchRequestDto($"Query {i}"));

        for (var i = 0; i < 5; i++)
            await service.SearchAsync(new SearchRequestDto("Query 11"));

        var popular = await service.GetPopularSearchesAsync(10);
        Assert.Equal(10, popular.Count);
        Assert.Equal("Query 11", popular[0].Query);
        Assert.Equal(6, popular[0].Count);
    }

    [Fact]
    public async Task SearchAsync_IgnoresTooShortQueriesForPopularStats()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await service.SearchAsync(new SearchRequestDto("a"));

        var popular = await service.GetPopularSearchesAsync();
        Assert.Empty(popular);
    }

    private static SearchService CreateService(KinshoutDbContext db)
    {
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Models.Advert>>(),
                It.IsAny<IReadOnlyList<Models.Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], ""));

        return new SearchService(db, openAi.Object);
    }
}
