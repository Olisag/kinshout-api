using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServicePaginationTests
{
    [Fact]
    public async Task SearchAsync_ReturnsPagedAdvertsWithTotals()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var advertIds = await SeedAdvertsAsync(db, user, category, 5);

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis(advertIds, [], "5 annonces"));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());

        var page1 = await service.SearchAsync(new SearchRequestDto("appartement", "annonces", Page: 1, PageSize: 2));
        Assert.Equal(2, page1.Adverts.Count);
        Assert.Empty(page1.Discussions);
        Assert.Equal(5, page1.Pagination.TotalAdverts);
        Assert.True(page1.Pagination.HasMoreAdverts);
        Assert.False(page1.Pagination.HasMoreDiscussions);

        var page2 = await service.SearchAsync(new SearchRequestDto("appartement", "annonces", Page: 2, PageSize: 2));
        Assert.Equal(2, page2.Adverts.Count);
        Assert.Equal(5, page2.Pagination.TotalAdverts);
        Assert.True(page2.Pagination.HasMoreAdverts);

        var page3 = await service.SearchAsync(new SearchRequestDto("appartement", "annonces", Page: 3, PageSize: 2));
        Assert.Single(page3.Adverts);
        Assert.False(page3.Pagination.HasMoreAdverts);
    }

    [Fact]
    public async Task SearchAsync_RecordsPopularQueryOnlyOnFirstPage()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateEmptySearchService(db);

        await service.SearchAsync(new SearchRequestDto("Appartement Gombe", Page: 1));
        await service.SearchAsync(new SearchRequestDto("Appartement Gombe", Page: 2));

        var popular = await service.GetPopularSearchesAsync();
        Assert.Single(popular.Items);
        Assert.Equal(1, popular.Items[0].Count);
    }

    [Fact]
    public async Task SearchAsync_ClampsPageSizeToMax()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var advertIds = await SeedAdvertsAsync(db, user, category, 60);

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis(advertIds, [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());
        var result = await service.SearchAsync(new SearchRequestDto("test", "annonces", Page: 1, PageSize: 999));

        Assert.Equal(50, result.Pagination.PageSize);
        Assert.Equal(50, result.Adverts.Count);
        Assert.True(result.Pagination.HasMoreAdverts);
    }

    private static SearchService CreateEmptySearchService(KinshoutDbContext db)
    {
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], ""));

        return new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache());
    }

    private static async Task<List<Guid>> SeedAdvertsAsync(
        KinshoutDbContext db,
        User user,
        Category category,
        int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var advert = new Advert
            {
                UserId = user.Id,
                CategoryId = category.Id,
                Title = $"Annonce {i + 1}",
                Description = "Appartement à Gombe",
                Location = "Gombe",
                IsPublished = true,
            };
            db.Adverts.Add(advert);
            ids.Add(advert.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }
}
