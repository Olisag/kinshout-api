using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchQueryUnderstandingTests
{
    [Fact]
    public async Task ResolveAsync_UsesAiHintsForComplexQuery()
    {
        var query = "Je suis a la recherche d une petite maison pas chere dans le quartier Gombe";
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.AnalyzeSearchQueryAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchQueryAnalysis(
                "maison pas cher gombe",
                SearchIntentHelper.Demande,
                "immobilier",
                "maison_a_louer",
                ["Gombe"],
                ["maison", "gombe", "pas cher"],
                0.92));

        var hints = await SearchQueryUnderstanding.ResolveAsync(
            query,
            openAi.Object,
            TestDbFactory.CreateMemoryCache(),
            CancellationToken.None);

        Assert.True(hints.UsedAiUnderstanding);
        Assert.Equal("immobilier", hints.ParentCategorySlug);
        Assert.Equal("maison_a_louer", hints.SubcategorySlug);
        Assert.Contains("Gombe", hints.LocationTerms);
        Assert.Contains("maison", hints.RetrievalTerms);
        openAi.Verify(x => x.AnalyzeSearchQueryAsync(query, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_SkipsAiForSimpleQuery()
    {
        var query = "appartement Gombe";
        var openAi = new Mock<IOpenAiService>();

        var hints = await SearchQueryUnderstanding.ResolveAsync(
            query,
            openAi.Object,
            TestDbFactory.CreateMemoryCache(),
            CancellationToken.None);

        Assert.False(hints.UsedAiUnderstanding);
        Assert.Equal("immobilier", hints.ParentCategorySlug);
        openAi.Verify(x => x.AnalyzeSearchQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_CachesAiUnderstanding()
    {
        var query = "Je suis a la recherche d une petite maison pas chere dans le quartier Gombe";
        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.AnalyzeSearchQueryAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiSearchQueryAnalysis(
                "maison pas cher gombe",
                SearchIntentHelper.Demande,
                "immobilier",
                "maison_a_louer",
                ["Gombe"],
                ["maison", "gombe"],
                0.9));

        var cache = TestDbFactory.CreateMemoryCache();
        await SearchQueryUnderstanding.ResolveAsync(query, openAi.Object, cache, CancellationToken.None);
        await SearchQueryUnderstanding.ResolveAsync(query, openAi.Object, cache, CancellationToken.None);

        openAi.Verify(x => x.AnalyzeSearchQueryAsync(query, It.IsAny<CancellationToken>()), Times.Once);
    }
}
