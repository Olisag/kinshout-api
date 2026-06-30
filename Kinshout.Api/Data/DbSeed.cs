using System.Text.Json;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Data;

public static class DbSeed
{
    public static async Task SeedAsync(KinshoutDbContext db)
    {
        await SeedApiClientsAsync(db);
        await SeedCategoriesAsync(db);
        await SeedPopularSearchesAsync(db);
    }

    private static async Task SeedApiClientsAsync(KinshoutDbContext db)
    {
        if (await db.ApiClients.AnyAsync())
            return;

        db.ApiClients.Add(new ApiClient
        {
            ClientId = ClientSeed.DefaultClientId,
            Name = "Kinshout Web App",
            SecretHash = "pending",
            AllowedOriginsJson = JsonSerializer.Serialize(ClientSeed.DefaultAllowedOrigins),
            IsActive = true,
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedCategoriesAsync(KinshoutDbContext db)
    {
        var systemCategories = new[]
        {
            new Category { Slug = "immobilier", Label = "Appartements à louer", Icon = "⌂", IsSystem = true },
            new Category { Slug = "vehicules_transport", Label = "Véhicules", Icon = "🚗", IsSystem = true },
            new Category { Slug = "emploi_services", Label = "Emplois", Icon = "▣", IsSystem = true },
            new Category { Slug = "electronique", Label = "Électroniques", Icon = "▯", IsSystem = true },
            new Category { Slug = "maison_jardin", Label = "Services", Icon = "⌘", IsSystem = true },
            new Category { Slug = Category.DiscussionSlug, Label = "Discussions", Icon = "💬", IsSystem = true },
        };

        var existingSlugs = await db.Categories
            .AsNoTracking()
            .Select(c => c.Slug)
            .ToListAsync();

        var missing = systemCategories
            .Where(c => !existingSlugs.Contains(c.Slug))
            .ToList();

        if (missing.Count == 0)
            return;

        db.Categories.AddRange(missing);
        await db.SaveChangesAsync();
    }

    private static async Task SeedPopularSearchesAsync(KinshoutDbContext db)
    {
        if (await db.SearchQueryStats.AnyAsync())
            return;

        var seeds = new (string Query, int Count)[]
        {
            ("Appartement à louer à Gombe", 48),
            ("Je cherche un chauffeur", 36),
            ("iPhone 13 pas cher", 31),
            ("Discussion sur Starlink", 27),
            ("Maison à vendre Bandal", 22),
            ("MacBook pas cher", 19),
            ("Cours particuliers maths", 16),
            ("Moto Yamaha Kinshasa", 14),
            ("Job chauffeur VTC", 12),
            ("Générateur solaire", 10),
        };

        db.SearchQueryStats.AddRange(seeds.Select((seed, index) => new SearchQueryStat
        {
            NormalizedQuery = SearchQueryHelper.CanonicalKey(seed.Query) ?? seed.Query.ToLowerInvariant(),
            DisplayQuery = seed.Query,
            SearchCount = seed.Count,
            LastSearchedAt = DateTime.UtcNow.AddDays(-index),
        }));

        await db.SaveChangesAsync();
    }
}
