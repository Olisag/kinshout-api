using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

public static class AiDiscussionCategoryCatalog
{
    public static readonly string[] TopicSlugs =
    [
        "sport",
        "politique",
        "societe",
        "education",
        "tech",
        "economie",
        "culture",
        "sante",
        "securite",
        "transport",
        "autres",
    ];

    public static bool IsTopicBucket(string slug) =>
        TopicSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase)
        || string.Equals(slug, "autres", StringComparison.OrdinalIgnoreCase);

    public static string ResolveTopicSlug(string? topicSlug, string? fallbackTopicSlug = null)
    {
        var key = NormalizeSlug(topicSlug) ?? NormalizeSlug(fallbackTopicSlug) ?? "autres";
        if (string.Equals(key, "autre_discussion", StringComparison.OrdinalIgnoreCase))
            return "autres";

        return IsTopicBucket(key) ? key : InferTopicFromUnknownSlug(key);
    }

    public static (string Slug, string Label, string Icon) DescribeTopic(string topicSlug) =>
        topicSlug switch
        {
            "sport" => ("sport", "Sport & foot", "⚽"),
            "politique" => ("politique", "Politique", "🏛️"),
            "societe" => ("societe", "Société & vie quotidienne", "👥"),
            "education" => ("education", "Éducation & examens", "🎓"),
            "tech" => ("tech", "Tech & internet", "📡"),
            "economie" => ("economie", "Économie & business", "💼"),
            "culture" => ("culture", "Culture & loisirs", "🎭"),
            "sante" => ("sante", "Santé", "🏥"),
            "securite" => ("securite", "Sécurité & justice", "🛡️"),
            "transport" => ("transport", "Transport & mobilité", "🚌"),
            "autres" => ("autres", "Autres", "📦"),
            _ => ("autres", "Autres", "📦"),
        };

    public static async Task<Category> GetOrCreateAsync(
        KinshoutDbContext db,
        string? topicSlug,
        IMemoryCache? cache = null,
        CancellationToken ct = default)
    {
        var resolved = ResolveTopicSlug(topicSlug);
        var (slug, label, icon) = DescribeTopic(resolved);

        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (existing is not null)
        {
            if (!existing.IsDiscussionTopic)
            {
                existing.IsDiscussionTopic = true;
                existing.IsAiGenerated = true;
                if (slug != "autres")
                {
                    existing.Label = label;
                    existing.Icon = icon;
                }

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
            IsDiscussionTopic = true,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        InvalidateCategoryCache(cache);
        return category;
    }

    public static async Task EnsureFromDiscussionsAsync(
        KinshoutDbContext db,
        IOpenAiService openAi,
        IMemoryCache cache,
        CancellationToken ct = default)
    {
        const int batchSize = 25;

        var uncategorized = await db.Discussions
            .Include(d => d.Category)
            .Where(d => d.TopicSlug == null
                || d.Category == null
                || d.Category.Slug == Category.DiscussionSlug
                || !d.Category.IsDiscussionTopic)
            .OrderBy(d => d.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (uncategorized.Count == 0)
            return;

        var topicCategories = await db.Categories
            .AsNoTracking()
            .Where(c => c.IsDiscussionTopic)
            .ToListAsync(ct);

        foreach (var discussion in uncategorized)
        {
            var analysis = await openAi.AnalyzeDiscussionAsync($"{discussion.Title}. {discussion.Body}", topicCategories, ct);
            var category = await GetOrCreateAsync(db, analysis.TopicSlug, cache, ct);
            discussion.CategoryId = category.Id;
            discussion.TopicSlug = category.Slug;
            discussion.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        InvalidateCategoryCache(cache);
    }

    public static void InvalidateCategoryCache(IMemoryCache? cache)
    {
        if (cache is null)
            return;

        var generation = cache.Get<int?>(ApiCacheKeys.DiscussionCategoriesGeneration) ?? 0;
        cache.Set(ApiCacheKeys.DiscussionCategoriesGeneration, generation + 1);
    }

    private static string? NormalizeSlug(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();

    private static string InferTopicFromUnknownSlug(string key)
    {
        if (ContainsAny(key, "sport", "foot", "football", "leopard", "leopards", "match", "coupe"))
            return "sport";

        if (ContainsAny(key, "politique", "fayulu", "opposition", "gouvernement", "tshisekedi", "election", "parlement"))
            return "politique";

        if (ContainsAny(key, "exetat", "examen", "ecole", "universite", "education", "etudiant", "diplome"))
            return "education";

        if (ContainsAny(key, "starlink", "internet", "tech", "iphone", "application", "reseau"))
            return "tech";

        if (ContainsAny(key, "dollar", "economie", "business", "entreprise", "marche", "dette"))
            return "economie";

        if (ContainsAny(key, "musique", "concert", "film", "culture", "art", "festival"))
            return "culture";

        if (ContainsAny(key, "sante", "hopital", "medical", "maladie", "docteur"))
            return "sante";

        if (ContainsAny(key, "police", "securite", "vol", "criminalite", "justice", "altercation"))
            return "securite";

        if (ContainsAny(key, "traffic", "embouteillage", "route", "transport", "motuka", "bus", "taxi"))
            return "transport";

        if (ContainsAny(key, "quartier", "kinshasa", "communaute", "societe", "vie"))
            return "societe";

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
