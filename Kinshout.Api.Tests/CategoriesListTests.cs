using Kinshout.Api.Controllers;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Tests;

public class CategoriesListTests
{
    [Fact]
    public async Task List_ExcludesDiscussionCategory()
    {
        await using var db = TestDbFactory.Create();
        db.Categories.AddRange(
            new Category { Slug = "immobilier", Label = "Immobilier", Icon = "⌂", IsSystem = true },
            new Category { Slug = Category.DiscussionSlug, Label = "Discussions", Icon = "💬", IsSystem = true },
            new Category { Slug = "electronique", Label = "Électronique", Icon = "▯", IsSystem = true });
        await db.SaveChangesAsync();

        var controller = new CategoriesController(db, TestDbFactory.CreateMemoryCache());
        var result = await controller.List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PagedResultDto<CategoryDto>>(ok.Value);
        Assert.Equal(2, paged.Items.Count);
        Assert.DoesNotContain(paged.Items, c => c.Slug == Category.DiscussionSlug);
    }
}
