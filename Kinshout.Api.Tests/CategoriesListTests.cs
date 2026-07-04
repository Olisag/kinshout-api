using Kinshout.Api.Controllers;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Tests;

public class CategoriesListTests
{
    [Fact]
    public async Task List_ReturnsOnlyAiGeneratedCategoriesWithPublishedAdverts()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var aiCategory = new Category
        {
            Slug = "telephone",
            Label = "Téléphones",
            Icon = "📱",
            IsAiGenerated = true,
        };
        var systemCategory = new Category
        {
            Slug = "immobilier",
            Label = "Immobilier",
            Icon = "⌂",
            IsSystem = true,
        };
        var discussion = new Category
        {
            Slug = Category.DiscussionSlug,
            Label = "Discussions",
            Icon = "💬",
            IsSystem = true,
        };
        db.Categories.AddRange(aiCategory, systemCategory, discussion);
        await db.SaveChangesAsync();

        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = aiCategory.Id,
            Title = "iPhone",
            Description = "Test",
            IsPublished = true,
            SubcategorySlug = "telephone",
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var controller = new CategoriesController(db, cache);
        var result = await controller.List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Single(paged.Items);
        Assert.Equal("telephones", paged.Items[0].Slug);
        Assert.True(paged.Items[0].IsAiGenerated);
        Assert.DoesNotContain(paged.Items, c => c.Slug == Category.DiscussionSlug);
        Assert.DoesNotContain(paged.Items, c => c.Slug == "immobilier");
    }

    [Fact]
    public async Task List_GeneratesAiCategoriesFromPublishedAdvertsWhenMissing()
    {
        await using var db = TestDbFactory.Create();
        var (user, systemCategory) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = systemCategory.Id,
            Title = "Toyota RAV4",
            Description = "Voiture Kinshasa",
            IsPublished = true,
            SubcategorySlug = "voiture",
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var controller = new CategoriesController(db, cache);
        var result = await controller.List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Single(paged.Items);
        Assert.Equal("vehicules", paged.Items[0].Slug);
        Assert.True(paged.Items[0].IsAiGenerated);

        var advert = await db.Adverts.SingleAsync();
        var assigned = await db.Categories.SingleAsync(c => c.IsAiGenerated);
        Assert.Equal(assigned.Id, advert.CategoryId);
    }

    [Fact]
    public async Task List_OrdersByPublishedAdvertCountMostPopularFirst()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var popular = new Category { Slug = "telephones", Label = "Téléphones", Icon = "📱", IsAiGenerated = true };
        var medium = new Category { Slug = "vehicules", Label = "Véhicules", Icon = "🚗", IsAiGenerated = true };
        var niche = new Category { Slug = "services", Label = "Services", Icon = "⌘", IsAiGenerated = true };
        db.Categories.AddRange(popular, medium, niche);
        await db.SaveChangesAsync();

        db.Adverts.AddRange(
            new Advert { UserId = user.Id, CategoryId = popular.Id, Title = "A", Description = "A", IsPublished = true, SubcategorySlug = "telephone" },
            new Advert { UserId = user.Id, CategoryId = popular.Id, Title = "B", Description = "B", IsPublished = true, SubcategorySlug = "telephone" },
            new Advert { UserId = user.Id, CategoryId = popular.Id, Title = "C", Description = "C", IsPublished = true, SubcategorySlug = "telephone" },
            new Advert { UserId = user.Id, CategoryId = medium.Id, Title = "D", Description = "D", IsPublished = true, SubcategorySlug = "voiture" },
            new Advert { UserId = user.Id, CategoryId = medium.Id, Title = "E", Description = "E", IsPublished = true, SubcategorySlug = "voiture" },
            new Advert { UserId = user.Id, CategoryId = niche.Id, Title = "F", Description = "F", IsPublished = true, SubcategorySlug = "plomberie" });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var controller = new CategoriesController(db, cache);
        var result = await controller.List(pageSize: 10);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Equal(["telephones", "vehicules", "services"], paged.Items.Select(c => c.Slug).ToArray());
    }

    [Fact]
    public async Task List_PutsAutresCategoryLastRegardlessOfPopularity()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var autres = new Category { Slug = "autres", Label = "Autres", Icon = "📦", IsAiGenerated = true };
        var phones = new Category { Slug = "telephones", Label = "Téléphones", Icon = "📱", IsAiGenerated = true };
        db.Categories.AddRange(autres, phones);
        await db.SaveChangesAsync();

        db.Adverts.AddRange(
            new Advert { UserId = user.Id, CategoryId = autres.Id, Title = "A", Description = "A", IsPublished = true, SubcategorySlug = "misc1" },
            new Advert { UserId = user.Id, CategoryId = autres.Id, Title = "B", Description = "B", IsPublished = true, SubcategorySlug = "misc2" },
            new Advert { UserId = user.Id, CategoryId = autres.Id, Title = "C", Description = "C", IsPublished = true, SubcategorySlug = "misc3" },
            new Advert { UserId = user.Id, CategoryId = phones.Id, Title = "D", Description = "D", IsPublished = true, SubcategorySlug = "telephone" });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var controller = new CategoriesController(db, cache);
        var result = await controller.List(pageSize: 10);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Equal("autres", paged.Items[^1].Slug);
    }

    [Fact]
    public void ResolveParentSlug_GroupsFineGrainedSubcategoriesIntoParentBuckets()
    {
        Assert.Equal("immobilier", AiCategoryCatalog.ResolveParentSlug("appartement_a_louer", null));
        Assert.Equal("vehicules", AiCategoryCatalog.ResolveParentSlug("moto", null));
        Assert.Equal("telephones", AiCategoryCatalog.ResolveParentSlug("telephone", null));
        Assert.Equal("emplois", AiCategoryCatalog.ResolveParentSlug("offre_emploi", null));
        Assert.Equal("services", AiCategoryCatalog.ResolveParentSlug("location_salle_fete", null));
        Assert.Equal("autres", AiCategoryCatalog.ResolveParentSlug("objet_rare_xyz", null));
    }

    [Fact]
    public async Task SyncContentAsync_ReassignsAdvertsToParentBuckets()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var fineGrained = new Category
        {
            Slug = "appartement_a_louer",
            Label = "Appartements à louer",
            Icon = "⌂",
            IsAiGenerated = true,
        };
        db.Categories.Add(fineGrained);
        await db.SaveChangesAsync();

        db.Adverts.Add(new Advert
        {
            UserId = user.Id,
            CategoryId = fineGrained.Id,
            Title = "Studio Gombe",
            Description = "Test",
            IsPublished = true,
            SubcategorySlug = "appartement_a_louer",
        });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        await AiCategoryCatalog.SyncContentAsync(db, cache);

        var advert = await db.Adverts.SingleAsync();
        var parent = await db.Categories.SingleAsync(c => c.Slug == "immobilier");
        Assert.Equal(parent.Id, advert.CategoryId);
    }

    [Fact]
    public async Task List_MigratesFineGrainedCategoriesToParentBucketsOnRequest()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var fineA = new Category { Slug = "telephone", Label = "Tel", Icon = "📱", IsAiGenerated = true };
        var fineB = new Category { Slug = "voiture", Label = "Voit", Icon = "🚗", IsAiGenerated = true };
        var fineC = new Category { Slug = "appartement_a_louer", Label = "Appt", Icon = "⌂", IsAiGenerated = true };
        db.Categories.AddRange(fineA, fineB, fineC);
        await db.SaveChangesAsync();

        db.Adverts.AddRange(
            new Advert { UserId = user.Id, CategoryId = fineA.Id, Title = "iPhone", Description = "T", IsPublished = true, SubcategorySlug = "telephone" },
            new Advert { UserId = user.Id, CategoryId = fineB.Id, Title = "Toyota", Description = "T", IsPublished = true, SubcategorySlug = "voiture" },
            new Advert { UserId = user.Id, CategoryId = fineC.Id, Title = "Studio", Description = "T", IsPublished = true, SubcategorySlug = "appartement_a_louer" });
        await db.SaveChangesAsync();

        var cache = TestDbFactory.CreateMemoryCache();
        var controller = new CategoriesController(db, cache);
        var result = await controller.List(pageSize: 20);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Equal(3, paged.TotalCount);
        Assert.Equal(3, paged.Items.Count);
        Assert.Equal(
            ["immobilier", "telephones", "vehicules"],
            paged.Items.Select(c => c.Slug).OrderBy(s => s).ToArray());
        Assert.All(paged.Items, c => Assert.True(AiCategoryCatalog.IsParentBucket(c.Slug)));
    }
}
