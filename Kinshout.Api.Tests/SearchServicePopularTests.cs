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
        await service.SearchAsync(new SearchRequestDto("  appart   gombe  "));

        var popular = await service.GetPopularSearchesAsync();
        Assert.Single(popular.Items);
        Assert.Equal(2, popular.Items[0].Count);
    }

    [Fact]
    public async Task SearchAsync_MergesSimilarQueriesWithDifferentWording()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await service.SearchAsync(new SearchRequestDto("Je cherche un chauffeur"));
        await service.SearchAsync(new SearchRequestDto("chauffeur VTC"));
        await service.SearchAsync(new SearchRequestDto("chauffeur"));

        var popular = await service.GetPopularSearchesAsync();
        Assert.Equal(2, popular.Items.Count);
        Assert.Equal(2, popular.Items.Single(p => p.Query.Contains("chauffeur", StringComparison.OrdinalIgnoreCase) && !p.Query.Contains("VTC", StringComparison.OrdinalIgnoreCase)).Count);
        Assert.Single(popular.Items, p => p.Query.Contains("VTC", StringComparison.OrdinalIgnoreCase));
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

        var popular = await service.GetPopularSearchesAsync(1, 10);
        Assert.Equal(10, popular.Items.Count);
        Assert.Equal("Query 11", popular.Items[0].Query);
        Assert.Equal(6, popular.Items[0].Count);
        Assert.True(popular.HasMore);
    }

    [Fact]
    public async Task SearchAsync_IgnoresTooShortQueriesForPopularStats()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await service.SearchAsync(new SearchRequestDto("a"));

        var popular = await service.GetPopularSearchesAsync();
        Assert.Empty(popular.Items);
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

        return new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
    }
}
