using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceComplexQueryTests
{
    [Fact]
    public async Task SearchAsync_ComplexFrenchQuery_UsesAiUnderstandingForRetrieval()
    {
        await using var db = TestDbFactory.Create();
        var (user, immobilier) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.AddRange(
            new Advert
            {
                UserId = user.Id,
                CategoryId = immobilier.Id,
                Title = "Maison familiale a louer",
                Description = "Petite maison pas chere, 2 chambres",
                Location = "Gombe, Kinshasa",
                IsPublished = true,
                SubcategorySlug = "maison_a_louer",
            },
            new Advert
            {
                UserId = user.Id,
                CategoryId = immobilier.Id,
                Title = "Villa luxe Ngaliema",
                Description = "Grande villa haut standing",
                Location = "Ngaliema, Kinshasa",
                IsPublished = true,
                SubcategorySlug = "maison_a_louer",
            });
        await db.SaveChangesAsync();

        var complexQuery = "Je suis a la recherche d une petite maison pas chere dans le quartier Gombe";
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.AnalyzeSearchQueryAsync(complexQuery, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchQueryAnalysis(
                "maison pas cher gombe",
                SearchIntentHelper.Demande,
                "immobilier",
                "maison_a_louer",
                ["Gombe"],
                ["maison", "gombe", "pas cher"],
                0.92));
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto(complexQuery, "annonces"));

        Assert.Single(result.Adverts);
        Assert.Contains("Gombe", result.Adverts[0].Location, StringComparison.OrdinalIgnoreCase);
        openAi.Verify(x => x.AnalyzeSearchQueryAsync(complexQuery, It.IsAny<CancellationToken>()), Times.Once);
    }
}
