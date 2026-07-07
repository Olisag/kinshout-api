using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceRelaxationTests
{
    [Fact]
    public async Task SearchAsync_RelaxesStructuredHintsWhenStrictFiltersReturnNothing()
    {
        await using var db = TestDbFactory.Create();
        var (user, immobilier) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = immobilier.Id,
            Title = "Appartement Limete",
            Description = "Bel appartement 2 chambres",
            Location = "Limete, Kinshasa",
            IsPublished = true,
            SubcategorySlug = "appartement_a_louer",
        });
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

        var result = await service.SearchAsync(new SearchRequestDto("appartement Gombe", "annonces"));

        Assert.Single(result.Adverts);
        Assert.Equal("Appartement Limete", result.Adverts[0].Title);
    }
}
