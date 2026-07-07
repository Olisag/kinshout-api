using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchKidsClothesTests
{
    [Fact]
    public void ExtractExpandedTerms_StripsVenteBoilerplate()
    {
        var expanded = SearchTermExpander.ExtractExpandedTerms("vente de vetement pour enfants");

        Assert.Contains("vetement", expanded);
        Assert.Contains("enfants", expanded);
        Assert.DoesNotContain(expanded, term => term is "vente" or "vendre" or "sale");
    }

    [Fact]
    public void Parse_ExtractsSubjectFromVenteDe()
    {
        var parsed = SearchQueryParser.Parse("vente de vêtement pour enfants");

        Assert.True(parsed.MatchedPattern);
        Assert.Equal(SearchIntentHelper.Offre, parsed.IntentHint);
        Assert.Contains("vetement", parsed.SubjectText, StringComparison.Ordinal);
        Assert.Contains("enfant", parsed.SubjectText, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseHints_InfersModeCategoryForKidsClothes()
    {
        var hints = SearchQueryResolver.ParseHints("vente de vêtement pour enfants");

        Assert.Equal("mode", hints.ParentCategorySlug);
    }

    [Fact]
    public async Task SearchAsync_KidsClothesQueryPrefersClothingOverCars()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var mode = new Category { Slug = "mode", Label = "Mode & accessoires", Icon = "👗" };
        var vehicules = new Category { Slug = "vehicules", Label = "Véhicules", Icon = "🚗" };
        db.Categories.AddRange(mode, vehicules);
        await db.SaveChangesAsync();

        db.Adverts.AddRange(
            new Advert
            {
                UserId = user.Id,
                CategoryId = vehicules.Id,
                Title = "Toyota RAV4 en vente urgente",
                Description = "Voiture à vendre, bon état",
                Location = "Kinshasa",
                IsPublished = true,
                ViewCount = 50_000,
            },
            new Advert
            {
                UserId = user.Id,
                CategoryId = mode.Id,
                Title = "Vente de vêtements pour enfants",
                Description = "Habits enfants taille 4-8 ans",
                Location = "Kinshasa",
                IsPublished = true,
                ViewCount = 10,
            });
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchAnalysis([], [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("vente de vêtement pour enfants", "annonces"));

        Assert.Single(result.Adverts);
        Assert.Equal("Vente de vêtements pour enfants", result.Adverts[0].Title);
    }
}
