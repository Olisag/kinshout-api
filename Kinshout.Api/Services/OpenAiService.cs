using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
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
    string Summary
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
            messages = new[] { new { role = "user", content = prompt } },
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
        var norm = text.ToLowerInvariant();
        var best = existing.FirstOrDefault(c => norm.Contains(c.Slug.Replace('_', ' ')))
            ?? existing.FirstOrDefault(c => c.Slug == "emploi_services")
            ?? existing.First();

        var intent = norm.Contains("cherche") || norm.Contains("recherche") ? "demande" : "offre";
        if (norm.Contains("discussion") || norm.Contains("avis"))
            intent = "discussion";

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
            0.55,
            $"Annonce classée en « {best.Label} » (règles locales)."
        );
    }

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
