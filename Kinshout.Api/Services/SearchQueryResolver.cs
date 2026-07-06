using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public sealed class ParsedSearchQuery
{
    public Category? AdvertCategory { get; init; }
    public string? SubcategorySlug { get; init; }
    public IReadOnlyList<string> LocationTerms { get; init; } = [];
    public Category? DiscussionTopic { get; init; }

    public bool IsAdvertCategoryBrowse =>
        AdvertCategory is not null && SubcategorySlug is null && LocationTerms.Count == 0;

    public bool IsStructuredAdvertSearch =>
        AdvertCategory is not null && (SubcategorySlug is not null || LocationTerms.Count > 0);

    public bool IsDiscussionTopicBrowse =>
        DiscussionTopic is not null && LocationTerms.Count == 0 && AdvertCategory is null;

    public bool IsDiscussionTopicSearch =>
        DiscussionTopic is not null && LocationTerms.Count > 0;
}

public static class SearchQueryResolver
{
    private static readonly string[] KinshasaCommunes =
    [
        "gombe", "ngaliema", "limete", "bandalungwa", "bandal", "kalamu", "macampagne", "masina",
        "mont ngafula", "ndjili", "kimbanseke", "maluku", "selembao", "kintambo", "barumbu", "lingwala",
        "ngaba", "makala", "kasa vubu", "bumbu", "matete", "lemba", "binza",
    ];

    public static ParsedSearchQuery Parse(
        string query,
        IReadOnlyList<Category> advertCategories,
        IReadOnlyList<Category> topicCategories)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ParsedSearchQuery();

        var normalized = SearchTextNormalizer.Normalize(query);
        if (normalized.Length < 2)
            return new ParsedSearchQuery();

        var locationTerms = ExtractLocationTerms(normalized);
        var textWithoutLocations = RemoveLocations(normalized, locationTerms);

        var advertCategory = TryMatchAdvertCategory(textWithoutLocations, advertCategories)
            ?? TryMatchAdvertCategory(normalized, advertCategories)
            ?? TryInferAdvertCategory(textWithoutLocations);

        var discussionTopic = TryMatchDiscussionTopic(normalized, topicCategories)
            ?? TryMatchDiscussionTopic(textWithoutLocations, topicCategories);

        if (ShouldSkipDiscussionTopic(normalized, textWithoutLocations, advertCategory))
            discussionTopic = null;

        string? subcategory = null;
        if (advertCategory is not null && locationTerms.Count > 0)
            subcategory = InferSubcategorySlug(textWithoutLocations, advertCategory.Slug);

        if (advertCategory is not null)
            advertCategory = advertCategories.FirstOrDefault(c =>
                c.Id == advertCategory.Id || c.Slug.Equals(advertCategory.Slug, StringComparison.OrdinalIgnoreCase))
                ?? advertCategory;

        if (discussionTopic is not null)
            discussionTopic = topicCategories.FirstOrDefault(c =>
                c.Id == discussionTopic.Id || c.Slug.Equals(discussionTopic.Slug, StringComparison.OrdinalIgnoreCase))
                ?? discussionTopic;

        return new ParsedSearchQuery
        {
            AdvertCategory = advertCategory,
            SubcategorySlug = subcategory,
            LocationTerms = locationTerms,
            DiscussionTopic = discussionTopic,
        };
    }

    private static bool ShouldSkipDiscussionTopic(string normalized, string textWithoutLocations, Category? advertCategory)
    {
        if (advertCategory is not null && !ContainsDiscussionIntent(normalized))
            return true;

        var tokens = textWithoutLocations
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        return tokens.All(token => token is "kinshasa" or "kin" or "rdc" or "congo");
    }

    private static bool ContainsDiscussionIntent(string normalized) =>
        normalized.Contains("discussion", StringComparison.Ordinal)
        || normalized.Contains("forum", StringComparison.Ordinal)
        || normalized.Contains("debat", StringComparison.Ordinal);

    private static Category? TryMatchAdvertCategory(string normalized, IReadOnlyList<Category> categories)
    {
        foreach (var category in categories)
        {
            if (category.IsDiscussionTopic || category.Slug == Category.DiscussionSlug)
                continue;

            var label = SearchTextNormalizer.Normalize(category.Label);
            var slug = SearchTextNormalizer.Normalize(category.Slug.Replace('_', ' '));
            if (normalized == label
                || normalized == slug
                || normalized.Contains(label, StringComparison.Ordinal)
                || label.Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(slug, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return null;
    }

    private static Category? TryInferAdvertCategory(string normalized)
    {
        var parentSlug = InferParentSlug(normalized);
        if (parentSlug is null)
            return null;

        var (slug, label, icon) = AiCategoryCatalog.DescribeParent(parentSlug);
        return new Category
        {
            Slug = slug,
            Label = label,
            Icon = icon,
            IsAiGenerated = true,
        };
    }

    private static Category? TryMatchDiscussionTopic(string normalized, IReadOnlyList<Category> topicCategories)
    {
        foreach (var topic in topicCategories)
        {
            var label = SearchTextNormalizer.Normalize(topic.Label);
            var slug = SearchTextNormalizer.Normalize(topic.Slug.Replace('_', ' '));
            if (normalized == label
                || normalized == slug
                || normalized.Contains(label, StringComparison.Ordinal)
                || label.Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(slug, StringComparison.Ordinal))
            {
                return topic;
            }
        }

        var inferredSlug = AiDiscussionCategoryCatalog.InferTopicSlugFromText(normalized);
        return topicCategories.FirstOrDefault(c =>
            c.Slug.Equals(inferredSlug, StringComparison.OrdinalIgnoreCase));
    }

    private static string? InferParentSlug(string normalized)
    {
        if (ContainsAny(normalized, "appartement", "appartements", "studio", "maison", "villa", "immobilier", "parcelle", "terrain", "ndako", "kofanda"))
            return "immobilier";

        if (ContainsAny(normalized, "voiture", "moto", "vehicule", "automobile", "toyota", "camion", "motuka"))
            return "vehicules";

        if (ContainsAny(normalized, "iphone", "samsung", "telephone", "smartphone", "tablette", "infinix", "tecno"))
            return "telephones";

        if (ContainsAny(normalized, "ordinateur", "macbook", "laptop", "pc ", " pc", "informatique"))
            return "informatique";

        if (ContainsAny(normalized, "electronique", "starlink", "generateur", "groupe electrogene", "panneau solaire", "playstation", "xbox"))
            return "electronique";

        if (ContainsAny(normalized, "emploi", "emplois", "travail", "job", "recrutement", "cv ", " cv", "stage", "mosala"))
            return "emplois";

        if (ContainsAny(normalized, "meuble", "canape", "decoration", "electromenager"))
            return "meubles";

        if (ContainsAny(normalized, "service", "plomberie", "nettoyage", "demenagement", "renovation"))
            return "services";

        if (ContainsAny(normalized, "vetement", "habit", "chaussure", "mode", "montre", "bijou", "sac "))
            return "mode";

        if (ContainsAny(normalized, "jouet", "jeu ", " jeu", "loisir"))
            return "jouets";

        return null;
    }

    internal static string? InferSubcategorySlug(string normalized, string parentSlug)
    {
        if (parentSlug == "immobilier")
        {
            if (ContainsAny(normalized, "studio"))
                return ContainsAny(normalized, "vendre", "vente", "sale") ? "studio_a_vendre" : "studio_a_louer";
            if (ContainsAny(normalized, "maison", "villa"))
                return ContainsAny(normalized, "vendre", "vente", "sale") ? "maison_a_vendre" : "maison_a_louer";
            if (ContainsAny(normalized, "appartement", "appartements", "flat", "apartment"))
                return ContainsAny(normalized, "vendre", "vente", "sale") ? "appartement_a_vendre" : "appartement_a_louer";
            if (ContainsAny(normalized, "parcelle", "terrain"))
                return "parcelle";
            if (ContainsAny(normalized, "bureau", "commercial"))
                return "bureau";
        }

        if (parentSlug == "vehicules")
        {
            if (ContainsAny(normalized, "moto", "scooter", "yamaha"))
                return "moto";
            if (ContainsAny(normalized, "camion", "bus"))
                return "camion";
            if (ContainsAny(normalized, "voiture", "automobile", "toyota", "jeep", "4x4"))
                return "voiture";
        }

        if (parentSlug == "telephones" && ContainsAny(normalized, "tablette", "ipad"))
            return "telephone";

        if (parentSlug == "informatique" && ContainsAny(normalized, "imprimante", "printer"))
            return "informatique";

        if (parentSlug == "emplois")
            return "offre_emploi";

        return null;
    }

    private static IReadOnlyList<string> ExtractLocationTerms(string normalized)
    {
        var matches = new List<string>();
        foreach (var commune in KinshasaCommunes.OrderByDescending(c => c.Length))
        {
            if (normalized.Contains(commune, StringComparison.Ordinal))
                matches.Add(commune);
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(NormalizeLocationLabel)
            .ToList();
    }

    private static string NormalizeLocationLabel(string commune) =>
        commune switch
        {
            "bandal" => "Bandal",
            "bandalungwa" => "Bandalungwa",
            "kasa vubu" => "Kasa-Vubu",
            "mont ngafula" => "Mont Ngafula",
            "ndjili" => "Ndjili",
            _ => char.ToUpperInvariant(commune[0]) + commune[1..],
        };

    private static string RemoveLocations(string normalized, IReadOnlyList<string> locationTerms)
    {
        var result = normalized;
        foreach (var location in locationTerms)
        {
            result = result.Replace(SearchTextNormalizer.Normalize(location), " ", StringComparison.Ordinal);
        }

        return string.Join(' ', result.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
