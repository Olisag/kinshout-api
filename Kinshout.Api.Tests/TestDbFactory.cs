using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

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

    public static async Task<KinshoutDbContext> CreateSqliteAsync()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KinshoutDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new KinshoutDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await DbSchemaPatcher.ApplyAsync(db);
        return db;
    }

    public static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    public static IAdvertDtoMapper CreateAdvertDtoMapper(string baseUrl = "https://api.test") =>
        new AdvertDtoMapper(new UploadUrlResolver(
            Options.Create(new UploadStorageSettings { PublicBaseUrl = baseUrl }),
            Mock.Of<IHttpContextAccessor>()));

    public static async Task<(User User, Category Category)> SeedUserAndCategoryAsync(
        KinshoutDbContext db,
        bool withWhatsApp = true)
    {
        var user = new User
        {
            Email = "test@kinshout.test",
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

    public static AiDiscussionAnalysis SampleDiscussionAnalysis(string slug = "societe") =>
        new(
            slug,
            slug switch
            {
                "sport" => "Sport & foot",
                "politique" => "Politique",
                "education" => "Éducation & examens",
                _ => "Société & vie quotidienne",
            },
            slug switch
            {
                "sport" => "⚽",
                "politique" => "🏛️",
                "education" => "🎓",
                _ => "👥",
            },
            0.9,
            "Sujet classé — échange communautaire Kinshasa.");
}
