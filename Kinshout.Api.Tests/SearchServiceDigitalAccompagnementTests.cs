using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceDigitalAccompagnementTests
{
    private const string Query = "Offre de service d'accompagnement numérique";

    [Fact]
    public void Parse_OffreDeService_ExtractsSubjectAndIntent()
    {
        var parsed = SearchQueryParser.Parse(Query);

        Assert.True(parsed.MatchedPattern);
        Assert.Equal(SearchIntentHelper.Offre, parsed.IntentHint);
        Assert.Contains("accompagnement", parsed.SubjectText, StringComparison.Ordinal);
        Assert.Contains("numerique", parsed.SubjectText, StringComparison.Ordinal);
        Assert.DoesNotContain("service", parsed.SubjectText, StringComparison.Ordinal);
        Assert.DoesNotContain("offre", parsed.SubjectText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractExpandedTerms_StripsOfferBoilerplate_KeepsSpecificTerms()
    {
        var expanded = SearchTermExpander.ExtractExpandedTerms(Query);

        Assert.Contains("accompagnement", expanded);
        Assert.Contains("numerique", expanded);
        Assert.DoesNotContain(expanded, term => term is "offre" or "offres" or "service" or "services");
    }

    [Fact]
    public void CoreSubjectTerms_RequiresSpecificTerms_NotGenericService()
    {
        var coreTerms = SearchRelevance.CoreSubjectTerms(Query);

        Assert.Contains("accompagnement", coreTerms);
        Assert.Contains("numerique", coreTerms);
        Assert.DoesNotContain(coreTerms, term => term is "offre" or "service");
    }

    [Fact]
    public void IsAdvertRelevant_RejectsSpamMatchingOnlyOnService()
    {
        var spam = new Advert
        {
            Title = "Amil baba in Pakistan, asli amil baba, black magic expert",
            Description = "Real amil baba service in Karachi, Lahore, Canada, USA, UK",
        };
        var relevant = new Advert
        {
            Title = "Accompagnement numérique pour PME",
            Description = "Offre de coaching et accompagnement numérique à Kinshasa",
        };

        const string genericServiceQuery = "service accompagnement numerique";

        Assert.False(SearchRelevance.IsAdvertRelevant(genericServiceQuery, spam));
        Assert.True(SearchRelevance.IsAdvertRelevant(genericServiceQuery, relevant));
    }

    [Fact]
    public async Task SearchAsync_DigitalAccompagnementOffer_ExcludesUnrelatedSpam()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var services = new Category { Slug = "services", Label = "Services", Icon = "🛠️" };
        db.Categories.Add(services);
        await db.SaveChangesAsync();

        db.Adverts.AddRange(
            new Advert
            {
                UserId = user.Id,
                CategoryId = services.Id,
                Title = "Amil baba in Pakistan, asli amil baba, black magic expert",
                Description = "Real amil baba service in Karachi, Lahore, Canada, USA, UK",
                Location = "Pakistan",
                IsPublished = true,
                ViewCount = 99_999,
            },
            new Advert
            {
                UserId = user.Id,
                CategoryId = services.Id,
                Title = "Accompagnement numérique pour PME",
                Description = "Coaching et accompagnement numérique à Kinshasa",
                Location = "Kinshasa",
                IsPublished = true,
                ViewCount = 5,
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

        var service = new SearchService(
            db,
            openAi.Object,
            TestDbFactory.CreateMemoryCache(),
            TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto(Query, "annonces"));

        Assert.Single(result.Adverts);
        Assert.Contains("Accompagnement numérique", result.Adverts[0].Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Adverts, advert =>
            advert.Title.Contains("Amil baba", StringComparison.OrdinalIgnoreCase));
    }
}
