using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchRetrievalCapTests
{
    [Fact]
    public async Task SearchAsync_CapsSemanticAdvertRetrievalAt500()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        for (var i = 0; i < 520; i++)
        {
            db.Adverts.Add(new Advert
            {
                UserId = user.Id,
                CategoryId = category.Id,
                Title = $"Appartement {i}",
                Description = "À louer Gombe",
                Location = "Gombe",
                IsPublished = true,
            });
        }

        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<Advert> loaded, IReadOnlyList<Discussion> _, CancellationToken __) =>
                new AiSearchAnalysis(loaded.Select(a => a.Id).ToList(), [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto("appartement", "annonces", PageSize: 50));

        Assert.Equal(SearchRetrieval.RetrieveCap, result.Pagination.TotalAdverts);
        Assert.Equal(50, result.Adverts.Count);
        Assert.True(result.Pagination.HasMoreAdverts);
    }

    [Fact]
    public async Task SearchAsync_SendsAtMost75CandidatesToOpenAi()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        for (var i = 0; i < 100; i++)
        {
            db.Adverts.Add(new Advert
            {
                UserId = user.Id,
                CategoryId = category.Id,
                Title = $"Annonce immobiliere {i}",
                Description = "Appartement disponible a louer",
                IsPublished = true,
            });
            db.Discussions.Add(new Discussion
            {
                UserId = user.Id,
                CategoryId = category.Id,
                Title = $"Forum logement {i}",
                Body = "Discussion sur un appartement",
            });
        }

        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<Advert> ads, IReadOnlyList<Discussion> discussions, CancellationToken __) =>
                new AiSearchAnalysis(ads.Select(a => a.Id).ToList(), discussions.Select(d => d.Id).ToList(), ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        await service.SearchAsync(new SearchRequestDto("appartement", "all", PageSize: 20));

        openAi.Verify(
            x => x.SearchAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<Advert>>(ads => ads.Count <= SearchRetrieval.OpenAiCandidateLimit),
                It.Is<IReadOnlyList<Discussion>>(discussions => discussions.Count <= SearchRetrieval.OpenAiCandidateLimit),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
