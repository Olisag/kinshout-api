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
}
