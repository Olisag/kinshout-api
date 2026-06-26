using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record AiAdvertAnalysis(
    string CategorySlug,
    string CategoryLabel,
    string CategoryIcon,
    bool CreateNewCategory,
    string Title,
    string Description,
    string Intent,
    string? Price,
    string? Location,
    IReadOnlyList<string> Tags,
    double Confidence,
    string Summary,
    bool RuleBasedFallback = false
);

public record AiSearchAnalysis(
    IReadOnlyList<Guid> AdvertIds,
    IReadOnlyList<Guid> DiscussionIds,
    string Summary
);

public interface IOpenAiService
{
    Task<AiAdvertAnalysis> AnalyzeAdvertAsync(string text, IReadOnlyList<Category> existingCategories, CancellationToken ct = default);
    Task<AiSearchAnalysis> SearchAsync(string query, IReadOnlyList<Advert> adverts, IReadOnlyList<Discussion> discussions, CancellationToken ct = default);
}

public partial class OpenAiService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiSettings> options,
    ILogger<OpenAiService> logger) : IOpenAiService
{
    private const string MultilingualSystemPrompt = """
        Tu es l'IA de Kinshout (petites annonces à Kinshasa, RDC).
        Les utilisateurs écrivent en français, anglais, lingala, ou un mélange (franglais, lingala + français).
        Comprends toujours le sens quelle que soit la langue ou le mélange.
        Exemples lingala courants: motuka=voiture, ndako=maison/logement, koteka=vendre, koluka=chercher,
        kolinga=vouloir, mosala=travail, mbongo=argent, telefone/simu=téléphone, kofanda=loger/se loger.
        Exemples: « Nazali koteka motuka » = vente de voiture; « Looking for apartment in Gombe » = recherche immo;
        « Nalingi ndako na Gombe » = cherche une maison à Gombe.
        Les champs destinés à l'interface (categoryLabel, summary, title, description) restent en français clair.
        Ne réduis pas la confiance uniquement parce que le texte n'est pas en français.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OpenAiSettings _settings = options.Value;

    public async Task<AiAdvertAnalysis> AnalyzeAdvertAsync(
        string text,
        IReadOnlyList<Category> existingCategories,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return FallbackAdvertAnalysis(text, existingCategories);

        var categoryList = string.Join("\n", existingCategories.Select(c => $"- {c.Slug}: {c.Label} ({c.Icon})"));
        var prompt = $$"""
            Tu es l'IA de Kinshout, plateforme d'annonces à Kinshasa (RDC).
            Analyse le texte d'annonce et réponds UNIQUEMENT en JSON:
            {
              "categorySlug": "slug existant OU nouveau slug snake_case",
              "categoryLabel": "libellé français",
              "categoryIcon": "emoji",
              "createNewCategory": true/false,
              "title": "titre court",
              "description": "description structurée",
              "intent": "offre|demande|discussion",
              "price": "prix ou null",
              "location": "quartier Kinshasa ou null",
              "tags": ["tag1","tag2"],
              "confidence": 0.0-1.0,
              "summary": "phrase explicative"
            }

            Règles:
            - Utilise une catégorie existante si elle convient (createNewCategory=false).
            - Sinon propose une nouvelle catégorie pertinente (createNewCategory=true).
            - Contexte Kinshasa: Gombe, Limete, Bandal, etc.
            - Langues acceptées: français, anglais, lingala (et mélanges). Interprète le sens, pas seulement les mots français.

            Catégories existantes:
            {{categoryList}}

            Texte: "{{text.Replace("\"", "\\\"")}}"
            """;

        try
        {
            var json = await ChatJsonAsync(prompt, ct);
            return ParseAdvertAnalysis(json, existingCategories, text);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI advert analysis failed, using fallback.");
            return FallbackAdvertAnalysis(text, existingCategories);
        }
    }

    public async Task<AiSearchAnalysis> SearchAsync(
        string query,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AiSearchAnalysis([], [], "Recherche vide.");

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return FallbackSearch(query, adverts, discussions);

        var advertLines = string.Join("\n", adverts.Select(a => $"{a.Id}|{a.Title}|{a.Description}|{a.Location}"));
        var discussionLines = string.Join("\n", discussions.Select(d => $"{d.Id}|{d.Title}|{d.Body}"));

        var prompt = $$"""
            Tu es l'IA de recherche Kinshout (Kinshasa, RDC).
            Pour la requête utilisateur, sélectionne les annonces et discussions pertinentes.
            Réponds UNIQUEMENT en JSON:
            {
              "advertIds": ["guid", ...],
              "discussionIds": ["guid", ...],
              "summary": "phrase courte expliquant les résultats"
            }

            Inclus annonces ET discussions liées sémantiquement (ex: "appartement Gombe" → annonces immo + discussions quartiers).
            Si peu de correspondances exactes, inclure les plus proches sémantiquement.
            La requête peut être en français, anglais ou lingala (ex: « motuka », « apartment », « ndako »).

            Requête: "{{query.Replace("\"", "\\\"")}}"

            Annonces (id|titre|description|lieu):
            {{advertLines}}

            Discussions (id|titre|corps):
            {{discussionLines}}
            """;

        try
        {
            var json = await ChatJsonAsync(prompt, ct);
            return ParseSearchAnalysis(json, adverts, discussions, query);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI search failed, using fallback.");
            return FallbackSearch(query, adverts, discussions);
        }
    }

    private async Task<string> ChatJsonAsync(string prompt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("OpenAI");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        var body = new
        {
            model = _settings.Model,
            messages = new object[]
            {
                new { role = "system", content = MultilingualSystemPrompt },
                new { role = "user", content = prompt },
            },
            temperature = 0.2,
            response_format = new { type = "json_object" },
        };

        using var response = await client.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
    }

    private static AiAdvertAnalysis ParseAdvertAnalysis(string json, IReadOnlyList<Category> existing, string text)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var slug = root.GetProperty("categorySlug").GetString() ?? "autre";
        var label = root.GetProperty("categoryLabel").GetString() ?? "Autre";
        var icon = root.GetProperty("categoryIcon").GetString() ?? "📦";
        var createNew = root.TryGetProperty("createNewCategory", out var cn) && cn.GetBoolean();
        var existingMatch = existing.FirstOrDefault(c => c.Slug == slug);

        if (existingMatch is not null)
        {
            createNew = false;
            label = existingMatch.Label;
            icon = existingMatch.Icon;
        }

        return new AiAdvertAnalysis(
            slug,
            label,
            icon,
            createNew,
            root.TryGetProperty("title", out var t) ? t.GetString() ?? text[..Math.Min(80, text.Length)] : text[..Math.Min(80, text.Length)],
            root.TryGetProperty("description", out var d) ? d.GetString() ?? text : text,
            root.TryGetProperty("intent", out var i) ? i.GetString() ?? "demande" : "demande",
            root.TryGetProperty("price", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null,
            root.TryGetProperty("location", out var l) && l.ValueKind != JsonValueKind.Null ? l.GetString() : null,
            root.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [],
            root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.85,
            root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : ""
        );
    }

    private static AiSearchAnalysis ParseSearchAnalysis(
        string json,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions,
        string query)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var advertIds = root.TryGetProperty("advertIds", out var a)
            ? a.EnumerateArray().Select(x => Guid.TryParse(x.GetString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList()
            : [];
        var discussionIds = root.TryGetProperty("discussionIds", out var d)
            ? d.EnumerateArray().Select(x => Guid.TryParse(x.GetString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList()
            : [];
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

        if (advertIds.Count == 0 && discussionIds.Count == 0)
            return FallbackSearch(query, adverts, discussions);

        return new AiSearchAnalysis(advertIds, discussionIds, summary);
    }

    private static AiAdvertAnalysis FallbackAdvertAnalysis(string text, IReadOnlyList<Category> existing)
    {
        var norm = NormalizeForMatch(text);
        var scores = existing.ToDictionary(c => c.Slug, _ => 0.0);

        foreach (var category in existing)
        {
            if (!CategoryKeywordRules.TryGetValue(category.Slug, out var keywords))
                continue;

            foreach (var keyword in keywords)
            {
                if (!norm.Contains(keyword, StringComparison.Ordinal))
                    continue;

                scores[category.Slug] += keyword.Length > 5 ? 1.5 : 1.0;
            }
        }

        var best = existing
            .OrderByDescending(c => scores.GetValueOrDefault(c.Slug))
            .ThenBy(c => c.Slug, StringComparer.Ordinal)
            .First();

        var bestScore = scores.GetValueOrDefault(best.Slug);
        if (bestScore <= 0)
        {
            best = existing.FirstOrDefault(c => c.Slug == "maison_jardin")
                ?? existing.FirstOrDefault(c => c.Slug != "discussion")
                ?? existing.First();
        }

        var intent = DetectFallbackIntent(norm, best.Slug);

        return new AiAdvertAnalysis(
            best.Slug,
            best.Label,
            best.Icon,
            false,
            text.Length > 80 ? text[..80] : text,
            text,
            intent,
            ExtractPrice(text),
            ExtractLocation(text),
            [],
            bestScore > 0 ? Math.Min(0.95, 0.45 + bestScore * 0.12) : 0.4,
            BuildFallbackSummary(best.Label, intent),
            RuleBasedFallback: true
        );
    }

    private static string NormalizeForMatch(string text) =>
        Regex.Replace(text.ToLowerInvariant().Trim(), @"\s+", " ");

    private static string DetectFallbackIntent(string norm, string categorySlug)
    {
        if (categorySlug == "discussion" || norm.Contains("discussion", StringComparison.Ordinal))
            return "discussion";

        if (OfferWords.Any(word => norm.Contains(word, StringComparison.Ordinal)))
            return "offre";

        if (DemandWords.Any(word => norm.Contains(word, StringComparison.Ordinal)))
            return "demande";

        return "demande";
    }

    private static string BuildFallbackSummary(string categoryLabel, string intent) =>
        intent switch
        {
            "offre" => $"Annonce classée en « {categoryLabel} » — le client propose un bien ou un service.",
            "discussion" => $"Sujet classé en « {categoryLabel} » — question ou échange communautaire.",
            _ => $"Recherche classée en « {categoryLabel} » — le client cherche un bien ou un service.",
        };

    private static readonly string[] OfferWords =
    [
        "vends", "vendre", "vend", "à vendre", "a vendre", "disponible", "propose", "offre", "loue", "louer",
        "for sale", "selling", "sell", "available", "rent out", "for rent", "to rent",
        "koteka", "kotia", "na koteka", "kotisa", "ezali na koteka",
    ];

    private static readonly string[] DemandWords =
    [
        "cherche", "recherche", "besoin", "veux", "acheter", "achète", "achete", "louer un", "louer une", "recrute",
        "looking for", "want", "need", "buy", "buying", "hire", "hiring",
        "koluka", "kolinga", "nazali koluka", "nazali kolinga", "nalingi",
    ];

    private static readonly Dictionary<string, string[]> CategoryKeywordRules = new(StringComparer.Ordinal)
    {
        ["immobilier"] =
        [
            "appartement", "apartment", "maison", "house", "studio", "villa", "loyer", "rent", "louer", "location",
            "colocation", "terrain", "bureau", "commerce", "gombe", "limete", "bandal", "kinshasa", "chambre",
            "immeuble", "parcelle", "ndako", "kofanda", "kotiya",
        ],
        ["vehicules_transport"] =
        [
            "voiture", "car", "moto", "motorcycle", "taxi", "chauffeur", "driver", "conducteur", "véhicule", "vehicule",
            "camion", "bus", "transport", "permis", "garage", "pièces auto", "auto", "scooter", "yamaha", "toyota",
            "motuka",
        ],
        ["electronique"] =
        [
            "iphone", "samsung", "téléphone", "telephone", "phone", "ordinateur", "computer", "laptop", "tablette",
            "tablet", "tv", "télévision", "console", "playstation", "xbox", "écouteurs", "chargeur", "starlink",
            "internet", "modem", "wifi", "macbook", "simu",
        ],
        ["emploi_services"] =
        [
            "emploi", "travail", "job", "work", "recrute", "cv", "salaire", "stage", "plombier", "électricien",
            "electrician", "electricien", "menuisier", "coiffeur", "nettoyage", "réparation", "reparation", "service",
            "freelance", "nounou", "vtc", "mosala", "kozwa mosala",
        ],
        ["maison_jardin"] =
        [
            "meuble", "furniture", "canapé", "canape", "sofa", "frigo", "réfrigérateur", "refrigerator", "cuisine",
            "ustensile", "décoration", "jardin", "outil", "vêtement", "vetement", "clothes", "robe", "dress",
            "chaussure", "shoes", "sac", "bag", "montre", "watch", "parfum", "maquillage", "coiffure", "beauté",
            "beaute", "bijou", "jewelry",
        ],
        ["discussion"] =
        [
            "discussion", "avis", "question", "forum", "conseil", "qu'en pensez", "politique", "société", "societe",
            "débat", "debat",
        ],
    };

    private static AiSearchAnalysis FallbackSearch(string query, IReadOnlyList<Advert> adverts, IReadOnlyList<Discussion> discussions)
    {
        var q = query.ToLowerInvariant();
        var advertIds = adverts
            .Where(a => a.Title.ToLowerInvariant().Contains(q)
                || a.Description.ToLowerInvariant().Contains(q)
                || (a.Location?.ToLowerInvariant().Contains(q) ?? false))
            .Select(a => a.Id)
            .ToList();
        var discussionIds = discussions
            .Where(d => d.Title.ToLowerInvariant().Contains(q) || d.Body.ToLowerInvariant().Contains(q))
            .Select(d => d.Id)
            .ToList();

        return new AiSearchAnalysis(advertIds, discussionIds, $"Résultats pour « {query} ».");
    }

    private static string? ExtractPrice(string text)
    {
        var m = PriceRegex().Match(text);
        return m.Success ? m.Value.Trim() : null;
    }

    private static string? ExtractLocation(string text)
    {
        foreach (var q in new[] { "Gombe", "Limete", "Bandal", "Binza", "Kinshasa" })
        {
            if (text.Contains(q, StringComparison.OrdinalIgnoreCase))
                return q;
        }
        return null;
    }

    [GeneratedRegex(@"(\d[\d\s]*)\s*\$|budget\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PriceRegex();
}

public static class CategoryResolver
{
    public static async Task<Category> ResolveOrCreateCategoryAsync(
        KinshoutDbContext db,
        AiAdvertAnalysis analysis,
        CancellationToken ct) =>
        await ResolveOrCreateCategoryAsync(db, analysis, cache: null, ct);

    public static async Task<Category> ResolveOrCreateCategoryAsync(
        KinshoutDbContext db,
        AiAdvertAnalysis analysis,
        IMemoryCache? cache,
        CancellationToken ct)
    {
        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Slug == analysis.CategorySlug, ct);
        if (existing is not null)
            return existing;

        if (!analysis.CreateNewCategory)
        {
            existing = await db.Categories.FirstOrDefaultAsync(c => c.Label == analysis.CategoryLabel, ct);
            if (existing is not null)
                return existing;
        }

        var category = new Category
        {
            Slug = Slugify(analysis.CategorySlug),
            Label = analysis.CategoryLabel,
            Icon = analysis.CategoryIcon,
            IsAiGenerated = true,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        cache?.Remove(ApiCacheKeys.CategoriesAll);
        return category;
    }

    private static string Slugify(string input)
    {
        var slug = input.ToLowerInvariant().Trim();
        slug = Regex.Replace(slug, @"[^a-z0-9_]+", "_");
        slug = Regex.Replace(slug, @"_+", "_").Trim('_');
        return string.IsNullOrEmpty(slug) ? $"cat_{Guid.NewGuid():N}"[..16] : slug;
    }
}
