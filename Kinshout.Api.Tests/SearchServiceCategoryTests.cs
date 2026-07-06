using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Moq;

namespace Kinshout.Api.Tests;

public class SearchServiceCategoryTests
{
    [Fact]
    public async Task SearchAsync_CategoryQueryReturnsAllCategoryAdvertsLikeListEndpoint()
    {
        await using var db = TestDbFactory.Create();
        var (user, immobilier) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var vehicules = new Category
        {
            Slug = "vehicules",
            Label = "Véhicules",
            Icon = "🚗",
            IsAiGenerated = true,
        };
        db.Categories.Add(vehicules);
        await db.SaveChangesAsync();

        for (var i = 0; i < 30; i++)
        {
            db.Adverts.Add(new Advert
            {
                UserId = user.Id,
                CategoryId = immobilier.Id,
                Title = $"Appartement {i}",
                Description = "À louer Gombe",
                IsPublished = true,
                SubcategorySlug = "appartement_a_louer",
            });
        }

        for (var i = 0; i < 5; i++)
        {
            db.Adverts.Add(new Advert
            {
                UserId = user.Id,
                CategoryId = vehicules.Id,
                Title = $"Toyota {i}",
                Description = "Voiture",
                IsPublished = true,
                SubcategorySlug = "voiture",
                ViewCount = 10_000,
            });
        }

        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var search = await service.SearchAsync(new SearchRequestDto("Appartements a louer", "annonces", PageSize: 50));
        var list = await new AdvertService(
            db,
            Mock.Of<IOpenAiService>(),
            Mock.Of<IAdvertModerationService>(),
            Mock.Of<IUploadStorage>(),
            TestDbFactory.CreateAdvertDtoMapper()).ListAsync(immobilier.Id, pageSize: 50);

        openAi.Verify(
            x => x.SearchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Advert>>(), It.IsAny<IReadOnlyList<Discussion>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.Equal(30, search.Pagination.TotalAdverts);
        Assert.Equal(30, list.TotalCount);
        Assert.Equal(list.TotalCount, search.Pagination.TotalAdverts);
    }

    [Fact]
    public async Task SearchAsync_StructuredQueryFiltersBySubcategoryAndLocation()
    {
        await using var db = TestDbFactory.Create();
        var (user, immobilier) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.AddRange(
            new Advert
            {
                UserId = user.Id,
                CategoryId = immobilier.Id,
                Title = "Appartement Gombe",
                Description = "Bel appart",
                Location = "Gombe, Kinshasa",
                IsPublished = true,
                SubcategorySlug = "appartement_a_louer",
            },
            new Advert
            {
                UserId = user.Id,
                CategoryId = immobilier.Id,
                Title = "Appartement Limete",
                Description = "Autre commune",
                Location = "Limete, Kinshasa",
                IsPublished = true,
                SubcategorySlug = "appartement_a_louer",
            },
            new Advert
            {
                UserId = user.Id,
                CategoryId = immobilier.Id,
                Title = "Maison Gombe",
                Description = "Grande maison",
                Location = "Gombe, Kinshasa",
                IsPublished = true,
                SubcategorySlug = "maison_a_louer",
            });
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("appartement Gombe", "annonces"));

        Assert.Single(result.Adverts);
        Assert.Equal("Appartement Gombe", result.Adverts[0].Title);
        openAi.Verify(
            x => x.SearchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Advert>>(), It.IsAny<IReadOnlyList<Discussion>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_SemanticQueryFindsLowViewAdvertsWithoutCap()
    {
        await using var db = TestDbFactory.Create();
        var (user, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        for (var i = 0; i < 5; i++)
        {
            db.Adverts.Add(new Advert
            {
                UserId = user.Id,
                CategoryId = category.Id,
                Title = $"Popular {i}",
                Description = "Generic listing",
                Location = "Kinshasa",
                IsPublished = true,
                ViewCount = 50_000,
            });
        }

        var hidden = new Advert
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Title = "Rare zebra keyword listing",
            Description = "Only match for zebraquery",
            Location = "Kinshasa",
            IsPublished = true,
            ViewCount = 1,
        };
        db.Adverts.Add(hidden);
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        openAi
            .Setup(x => x.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Advert>>(),
                It.IsAny<IReadOnlyList<Discussion>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<Advert> adverts, IReadOnlyList<Discussion> _, CancellationToken __) =>
                new AiSearchAnalysis(adverts.Select(a => a.Id).ToList(), [], ""));

        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());
        var result = await service.SearchAsync(new SearchRequestDto("zebraquery", "annonces"));

        Assert.Single(result.Adverts);
        Assert.Equal(hidden.Id, result.Adverts[0].Id);
    }

    [Fact]
    public async Task SearchAsync_DiscussionTopicQueryReturnsAllTopicDiscussions()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);
        var sport = new Category
        {
            Slug = "sport",
            Label = "Sport & foot",
            Icon = "⚽",
            IsDiscussionTopic = true,
            IsAiGenerated = true,
        };
        var politique = new Category
        {
            Slug = "politique",
            Label = "Politique",
            Icon = "🏛️",
            IsDiscussionTopic = true,
            IsAiGenerated = true,
        };
        db.Categories.AddRange(sport, politique);
        await db.SaveChangesAsync();

        for (var i = 0; i < 4; i++)
        {
            db.Discussions.Add(new Discussion
            {
                UserId = user.Id,
                CategoryId = sport.Id,
                TopicSlug = sport.Slug,
                Title = $"Match {i}",
                Body = "Leopards",
            });
        }

        db.Discussions.Add(new Discussion
        {
            UserId = user.Id,
            CategoryId = politique.Id,
            TopicSlug = politique.Slug,
            Title = "Election",
            Body = "Politique",
        });
        await db.SaveChangesAsync();

        var openAi = new Mock<IOpenAiService>();
        var service = new SearchService(db, openAi.Object, TestDbFactory.CreateMemoryCache(), TestDbFactory.CreateAdvertDtoMapper());

        var result = await service.SearchAsync(new SearchRequestDto("Sport & foot", "discussions"));

        Assert.Equal(4, result.Pagination.TotalDiscussions);
        openAi.Verify(
            x => x.SearchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Advert>>(), It.IsAny<IReadOnlyList<Discussion>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
