using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

public static class AiCategoryCatalog
{
    private static readonly string[] SeededPopularQueries =
    [
        "Appartement à louer à Gombe",
        "Je cherche un chauffeur",
        "iPhone 13 pas cher",
        "Discussion sur Starlink",
        "Maison à vendre Bandal",
        "MacBook pas cher",
        "Cours particuliers maths",
        "Moto Yamaha Kinshasa",
        "Job chauffeur VTC",
        "Générateur solaire",
    ];

    public static (string Slug, string Label, string Icon) Describe(string? subcategorySlug, string? fallbackCategorySlug = null)
    {
        var slug = string.IsNullOrWhiteSpace(subcategorySlug)
            ? string.IsNullOrWhiteSpace(fallbackCategorySlug) ? "immobilier_kinshasa" : fallbackCategorySlug.Trim().ToLowerInvariant()
            : subcategorySlug.Trim().ToLowerInvariant();

        return slug switch
        {
            "appartement_a_louer" => ("appartement_a_louer", "Appartements à louer", "⌂"),
            "maison_a_louer" => ("maison_a_louer", "Maisons à louer", "🏠"),
            "parcelle" => ("parcelle", "Parcelles & terrains", "📐"),
            "appartement_a_vendre" => ("appartement_a_vendre", "Appartements à vendre", "⌂"),
            "maison_a_vendre" => ("maison_a_vendre", "Maisons à vendre", "🏠"),
            "studio_a_louer" => ("studio_a_louer", "Studios à louer", "🏢"),
            "telephone" => ("telephone", "Téléphones & tablettes", "📱"),
            "informatique" => ("informatique", "Informatique & électronique", "💻"),
            "energie" => ("energie", "Groupes électrogènes & solaire", "⚡"),
            "voiture" => ("voiture", "Voitures", "🚗"),
            "moto" => ("moto", "Motos & scooters", "🏍️"),
            "camion" => ("camion", "Camions & bus", "🚌"),
            "offre_emploi" => ("offre_emploi", "Emplois", "▣"),
            "meubles" => ("meubles", "Maison & meubles", "🛋️"),
            "services" => ("services", "Services", "⌘"),
            "mode" => ("mode", "Mode & accessoires", "👗"),
            "jouets" => ("jouets", "Jouets & jeux", "🎮"),
            "vehicules_transport" => ("vehicules_transport", "Véhicules", "🚗"),
            "emploi_services" => ("emploi_services", "Emplois & services", "▣"),
            "electronique" => ("electronique", "Électronique", "▯"),
            "maison_jardin" => ("maison_jardin", "Maison & jardin", "🏡"),
            "immobilier" => ("immobilier_kinshasa", "Immobilier Kinshasa", "⌂"),
            _ when !string.IsNullOrWhiteSpace(fallbackCategorySlug) && slug == fallbackCategorySlug.Trim().ToLowerInvariant()
                => (slug, HumanizeSlug(slug), IconForCategory(slug)),
            _ => (slug, HumanizeSlug(slug), IconForCategory(slug)),
        };
    }

    private static string IconForCategory(string slug) => slug switch
    {
        "electronique" or "telephone" or "informatique" => "▯",
        "vehicules_transport" or "voiture" or "moto" => "🚗",
        "emploi_services" or "offre_emploi" => "▣",
        "maison_jardin" or "meubles" or "services" => "⌘",
        "immobilier" or "immobilier_kinshasa" => "⌂",
        _ => "📦",
    };

    public static async Task<Category> GetOrCreateAsync(
        KinshoutDbContext db,
        string? subcategorySlug,
        string? fallbackCategorySlug = null,
        IMemoryCache? cache = null,
        CancellationToken ct = default)
    {
        var (slug, label, icon) = Describe(subcategorySlug, fallbackCategorySlug);

        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (existing is not null)
            return existing;

        var category = new Category
        {
            Slug = slug,
            Label = label,
            Icon = icon,
            IsAiGenerated = true,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        InvalidateCategoryCache(cache);
        return category;
    }

    public static async Task SyncContentAsync(KinshoutDbContext db, IMemoryCache cache, CancellationToken ct = default)
    {
        var seeded = await db.SearchQueryStats
            .Where(s => SeededPopularQueries.Contains(s.DisplayQuery))
            .ToListAsync(ct);
        if (seeded.Count > 0)
        {
            db.SearchQueryStats.RemoveRange(seeded);
            await db.SaveChangesAsync(ct);
            cache.Remove(ApiCacheKeys.PopularSearches);
        }

        var adverts = await db.Adverts
            .Where(a => a.IsPublished)
            .Select(a => new { a.Id, a.SubcategorySlug, a.CategoryId })
            .ToListAsync(ct);

        if (adverts.Count == 0)
            return;

        var categoryById = await db.Categories.ToDictionaryAsync(c => c.Id, ct);
        var targetByAdvert = new Dictionary<Guid, Category>();

        foreach (var group in adverts.GroupBy(a => a.SubcategorySlug ?? "immobilier"))
        {
            var aiCategory = await GetOrCreateAsync(db, group.Key, "immobilier", cache, ct);
            foreach (var advert in group)
                targetByAdvert[advert.Id] = aiCategory;
        }

        var toUpdate = await db.Adverts
            .Where(a => a.IsPublished)
            .ToListAsync(ct);

        var changed = false;
        foreach (var advert in toUpdate)
        {
            if (!targetByAdvert.TryGetValue(advert.Id, out var target))
                continue;

            if (categoryById.TryGetValue(advert.CategoryId, out var current) && current.IsAiGenerated)
                continue;

            advert.CategoryId = target.Id;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            InvalidateCategoryCache(cache);
        }
    }

    public static void InvalidateCategoryCache(IMemoryCache? cache)
    {
        if (cache is null)
            return;

        cache.Remove(ApiCacheKeys.CategoriesAll);
    }

    private static string HumanizeSlug(string slug) =>
        string.Join(' ', slug.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Replace("a louer", "à louer", StringComparison.OrdinalIgnoreCase)
            .Replace("a vendre", "à vendre", StringComparison.OrdinalIgnoreCase);
}
