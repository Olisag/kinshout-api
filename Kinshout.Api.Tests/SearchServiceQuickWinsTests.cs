using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceQuickWinsTests
{
    [Fact]
    public async Task SearchAsync_ConfidentLocalRank_SkipsOpenAi()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var moto = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Yamaha moto 125",
            Description = "Moto en bon état",
            SubcategorySlug = "moto",
            IsPublished = true,
        };
        var voiture = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Toyota RAV4",
            Description = "Voiture familiale",
            SubcategorySlug = "voiture",
            IsPublished = true,
        };
        db.Adverts.AddRange(moto, voiture);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], "ai"));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("moto", "annonces"));

        Assert.Single(result.Adverts);
        Assert.Equal(moto.Id, result.Adverts[0].Id);
        openAi.Verify(
            x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_SubcategoryHintWithAdvertsOnly_SkipsOpenAi()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var apartment = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Bel appartement Gombe",
            Description = "2 chambres meublé",
            SubcategorySlug = "appartement_a_louer",
            IsPublished = true,
        };
        db.Adverts.Add(apartment);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], "ai"));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("je cherche un appartement", "annonces"));

        Assert.Single(result.Adverts);
        Assert.Equal(apartment.Id, result.Adverts[0].Id);
        openAi.Verify(
            x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
