using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceDemandQueryTests
{
    [Fact]
    public async Task SearchAsync_DemandPhraseMoto_ReturnsSameMotoResultsAsBareQuery()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var moto = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Yamaha 125",
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
        var unrelated = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Je cherche un colocataire",
            Description = "Recherche colocation Gombe",
            IsPublished = true,
        };
        db.Adverts.AddRange(moto, voiture, unrelated);
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

        var bare = await service.SearchAsync(new SearchRequestDto("moto", "annonces"));
        var demand = await service.SearchAsync(new SearchRequestDto("je cherche une moto", "annonces"));

        Assert.Single(bare.Adverts);
        Assert.Equal(moto.Id, bare.Adverts[0].Id);
        Assert.Single(demand.Adverts);
        Assert.Equal(moto.Id, demand.Adverts[0].Id);
    }

    [Fact]
    public async Task SearchAsync_MisspelledApartmentQuery_MatchesCorrectListings()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var apartment = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Bel appartement à louer",
            Description = "2 chambres meublé Gombe",
            SubcategorySlug = "appartement_a_louer",
            IsPublished = true,
        };
        var other = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Studio disponible",
            Description = "Petit studio Bandal",
            SubcategorySlug = "studio_a_louer",
            IsPublished = true,
        };
        db.Adverts.AddRange(apartment, other);
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

        var correct = await service.SearchAsync(new SearchRequestDto("je cherche un appartement a louer", "annonces"));
        var misspelled = await service.SearchAsync(new SearchRequestDto("je cherche un apartement a louer", "annonces"));

        Assert.Single(correct.Adverts);
        Assert.Equal(apartment.Id, correct.Adverts[0].Id);
        Assert.Single(misspelled.Adverts);
        Assert.Equal(apartment.Id, misspelled.Adverts[0].Id);
    }
}
