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

    public static readonly string[] ParentBucketSlugs =
    [
        "immobilier",
        "vehicules",
        "telephones",
        "informatique",
        "electronique",
        "emplois",
        "meubles",
        "services",
        "mode",
        "jouets",
        "autres",
    ];

    public static bool IsParentBucket(string slug) =>
        ParentBucketSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase);

    public static string ResolveParentSlug(string? subcategorySlug, string? fallbackCategorySlug = null)
    {
        var key = NormalizeSlug(subcategorySlug) ?? NormalizeSlug(fallbackCategorySlug) ?? "immobilier";

        return key switch
        {
            "immobilier" or "immobilier_kinshasa" or "appartement_a_louer" or "maison_a_louer"
                or "studio_a_louer" or "appartement_a_vendre" or "maison_a_vendre" or "parcelle" => "immobilier",

            "vehicules" or "vehicules_transport" or "voiture" or "moto" or "camion" => "vehicules",

            "telephones" or "telephone" => "telephones",

            "informatique" => "informatique",

            "electronique" or "energie" => "electronique",

            "emplois" or "emploi_services" or "offre_emploi" => "emplois",

            "meubles" or "maison_jardin" => "meubles",

            "services" => "services",

            "mode" => "mode",

            "jouets" => "jouets",

            "autres" => "autres",

            _ => InferParentFromUnknownSlug(key),
        };
    }

    public static (string Slug, string Label, string Icon) DescribeParent(string parentSlug) =>
        parentSlug switch
        {
            "immobilier" => ("immobilier", "Immobilier", "⌂"),
            "vehicules" => ("vehicules", "Véhicules", "🚗"),
            "telephones" => ("telephones", "Téléphones & tablettes", "📱"),
            "informatique" => ("informatique", "Informatique", "💻"),
            "electronique" => ("electronique", "Électronique & énergie", "▯"),
            "emplois" => ("emplois", "Emplois", "▣"),
            "meubles" => ("meubles", "Maison & meubles", "🛋️"),
            "services" => ("services", "Services", "⌘"),
            "mode" => ("mode", "Mode & accessoires", "👗"),
            "jouets" => ("jouets", "Jouets & loisirs", "🎮"),
            _ => ("autres", "Autres", "📦"),
        };

    public static async Task<Category> GetOrCreateAsync(
        KinshoutDbContext db,
        string? subcategorySlug,
        string? fallbackCategorySlug = null,
        IMemoryCache? cache = null,
        CancellationToken ct = default)
    {
        var parentSlug = ResolveParentSlug(subcategorySlug, fallbackCategorySlug);
        var (slug, label, icon) = DescribeParent(parentSlug);

        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (existing is not null)
        {
            if (!existing.IsAiGenerated && IsParentBucket(slug))
            {
                existing.IsAiGenerated = true;
                existing.Label = label;
                existing.Icon = icon;
                await db.SaveChangesAsync(ct);
                InvalidateCategoryCache(cache);
            }

            return existing;
        }

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

    public static async Task EnsureFromAdvertsAsync(
        KinshoutDbContext db,
        IMemoryCache cache,
        CancellationToken ct = default)
    {
        if (!await db.Adverts.AnyAsync(a => a.IsPublished, ct))
            return;

        var activeBrowseCategoryCount = await db.Categories
            .AsNoTracking()
            .Where(c => c.IsAiGenerated && c.Slug != Category.DiscussionSlug)
            .CountAsync(c => c.Adverts.Any(a => a.IsPublished), ct);

        var hasNonParentAssignments = await db.Adverts
            .AsNoTracking()
            .Where(a => a.IsPublished)
            .AnyAsync(a => !ParentBucketSlugs.Contains(a.Category.Slug), ct);

        if (!hasNonParentAssignments
            && activeBrowseCategoryCount <= ParentBucketSlugs.Length)
        {
            var assignments = await db.Adverts
                .AsNoTracking()
                .Where(a => a.IsPublished)
                .Select(a => new { a.SubcategorySlug, CategorySlug = a.Category.Slug })
                .ToListAsync(ct);

            if (assignments.All(a =>
                    IsParentBucket(a.CategorySlug)
                    && ResolveParentSlug(a.SubcategorySlug, a.CategorySlug) == a.CategorySlug))
                return;
        }

        await SyncContentAsync(db, cache, ct);
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
            .AsNoTracking()
            .Where(a => a.IsPublished)
            .Select(a => new { a.Id, a.SubcategorySlug, a.CategoryId, CategorySlug = a.Category.Slug })
            .ToListAsync(ct);

        if (adverts.Count == 0)
            return;

        var targetByAdvert = new Dictionary<Guid, Category>();

        foreach (var group in adverts.GroupBy(a => GroupKey(a.SubcategorySlug, a.CategorySlug)))
        {
            var aiCategory = await GetOrCreateAsync(db, group.Key, group.Key, cache, ct);
            foreach (var advert in group)
                targetByAdvert[advert.Id] = aiCategory;
        }

        var assignments = await db.Adverts
            .AsNoTracking()
            .Where(a => a.IsPublished)
            .Select(a => new { a.Id, a.CategoryId })
            .ToListAsync(ct);

        var updatesByCategory = new Dictionary<Guid, List<Guid>>();
        foreach (var advert in assignments)
        {
            if (!targetByAdvert.TryGetValue(advert.Id, out var target))
                continue;

            if (advert.CategoryId == target.Id)
                continue;

            if (!updatesByCategory.TryGetValue(target.Id, out var ids))
            {
                ids = [];
                updatesByCategory[target.Id] = ids;
            }

            ids.Add(advert.Id);
        }

        if (updatesByCategory.Count == 0)
            return;

        foreach (var (categoryId, advertIds) in updatesByCategory)
        {
            foreach (var advertId in advertIds)
            {
                var tracked = db.Adverts.Local.FirstOrDefault(a => a.Id == advertId);
                if (tracked is not null)
                {
                    tracked.CategoryId = categoryId;
                    continue;
                }

                var stub = new Advert { Id = advertId, CategoryId = categoryId };
                db.Adverts.Attach(stub);
                db.Entry(stub).Property(a => a.CategoryId).IsModified = true;
            }
        }

        await db.SaveChangesAsync(ct);
        InvalidateCategoryCache(cache);
    }

    public static void InvalidateCategoryCache(IMemoryCache? cache)
    {
        if (cache is null)
            return;

        var generation = cache.Get<int?>(ApiCacheKeys.CategoriesGeneration) ?? 0;
        cache.Set(ApiCacheKeys.CategoriesGeneration, generation + 1);
    }

    private static string GroupKey(string? subcategorySlug, string? categorySlug) =>
        ResolveParentSlug(subcategorySlug, categorySlug);

    private static string? NormalizeSlug(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();

    private static string InferParentFromUnknownSlug(string key)
    {
        if (ContainsAny(key, "appartement", "maison", "studio", "parcelle", "immobilier", "terrain", "bureau", "villa", "immeuble"))
            return "immobilier";

        if (ContainsAny(key, "voiture", "moto", "camion", "vehicule", "automobile", "scooter", "velo"))
            return "vehicules";

        if (ContainsAny(key, "iphone", "samsung", "telephone", "tablette", "smartphone"))
            return "telephones";

        if (ContainsAny(key, "ordinateur", "laptop", "macbook", "informatique", "imprimante"))
            return "informatique";

        if (ContainsAny(key, "tv", "tele", "console", "playstation", "xbox", "electronique", "energie", "generateur", "solaire"))
            return "electronique";

        if (ContainsAny(key, "emploi", "job", "recrutement", "stage", "cv"))
            return "emplois";

        if (ContainsAny(key, "meuble", "canape", "frigo", "decoration", "jardin"))
            return "meubles";

        if (ContainsAny(key, "service", "plomberie", "demenagement", "nettoyage", "reparation", "location"))
            return "services";

        if (ContainsAny(key, "vetement", "mode", "chaussure", "montre", "bijou", "sac"))
            return "mode";

        if (ContainsAny(key, "jouet", "jeu", "loisir"))
            return "jouets";

        return "autres";
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
