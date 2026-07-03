using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Tests;

internal static class TestDbFactory
{
    public static KinshoutDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<KinshoutDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;

        return new KinshoutDbContext(options);
    }

    public static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    public static async Task<(User User, Category Category)> SeedUserAndCategoryAsync(
        KinshoutDbContext db,
        bool withWhatsApp = true)
    {
        var user = new User
        {
            Email = "test@kinshout.test",
            Username = "test_user",
            DisplayName = "Test User",
            WhatsAppNumber = withWhatsApp ? "+243900000001" : null,
        };

        var category = new Category
        {
            Slug = "immobilier",
            Label = "Immobilier",
            Icon = "⌂",
            IsSystem = true,
        };

        db.Users.Add(user);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return (user, category);
    }

    public static AiAdvertAnalysis SampleAnalysis(string slug = "immobilier") =>
        new(
            slug,
            "Immobilier",
            "⌂",
            false,
            "Appartement à Gombe",
            "Bel appartement 2 chambres à Gombe.",
            "demande",
            "500 $",
            "Gombe",
            ["2 chambres", "Gombe"],
            0.9,
            "Recherche d'appartement à Gombe.");
}
