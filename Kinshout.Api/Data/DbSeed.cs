using System.Text.Json;
using Kinshout.Api.Models;
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
            AllowedOriginsJson = JsonSerializer.Serialize(new[]
            {
                "https://kinshout.vercel.app",
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:5280",
            }),
            IsActive = true,
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedCategoriesAsync(KinshoutDbContext db)
    {
        if (await db.Categories.AnyAsync())
            return;

        var categories = new[]
        {
            new Category { Slug = "immobilier", Label = "Appartements à louer", Icon = "⌂", IsSystem = true },
            new Category { Slug = "vehicules_transport", Label = "Véhicules", Icon = "🚗", IsSystem = true },
            new Category { Slug = "emploi_services", Label = "Emplois", Icon = "▣", IsSystem = true },
            new Category { Slug = "electronique", Label = "Électroniques", Icon = "▯", IsSystem = true },
            new Category { Slug = "maison_jardin", Label = "Services", Icon = "⌘", IsSystem = true },
            new Category { Slug = "discussion", Label = "Discussions", Icon = "💬", IsSystem = true },
        };

        db.Categories.AddRange(categories);
        await db.SaveChangesAsync();
    }
}
