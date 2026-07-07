using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Tests;

public class CategoryResolverTests
{
    [Fact]
    public async Task ResolveOrCreateCategoryAsync_ExistingSlug_ReturnsExistingCategory()
    {
        await using var db = TestDbFactory.Create();
        var (_, category) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var resolved = await CategoryResolver.ResolveOrCreateCategoryAsync(
            db,
            TestDbFactory.SampleAnalysis("immobilier"),
            CancellationToken.None);

        Assert.Equal(category.Id, resolved.Id);
        Assert.Equal(1, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task ResolveOrCreateCategoryAsync_NewCategory_CreatesAiCategory()
    {
        await using var db = TestDbFactory.Create();
        await TestDbFactory.SeedUserAndCategoryAsync(db);

        var resolved = await CategoryResolver.ResolveOrCreateCategoryAsync(
            db,
            TestDbFactory.SampleAnalysis("coiffure_domicile") with
            {
                CategoryLabel = "Coiffure à domicile",
                CategoryIcon = "💇",
                CreateNewCategory = true,
            },
            CancellationToken.None);

        Assert.Equal("autres", resolved.Slug);
        Assert.Equal("Autres", resolved.Label);
        Assert.True(resolved.IsAiGenerated);
        Assert.Equal(2, await db.Categories.CountAsync());
    }
}
