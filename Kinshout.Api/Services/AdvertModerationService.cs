using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public class AdvertModerationService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiSettings> options,
    ILogger<AdvertModerationService> logger) : IAdvertModerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> SexualModerationCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "sexual",
        "sexual/minors",
    };

    private static readonly string[] TextFallbackBlockPatterns =
    [
        @"\b(porno|pornograph|sexe\s+payant|escort|nud(?:e|ité)|onlyfans|plan\s+cul)\b",
        @"\b(prostitu|massage\s+érotique|massage\s+erotique)\b",
    ];

    private readonly OpenAiSettings _settings = options.Value;

    public async Task EnsureTextAllowedAsync(string text, CancellationToken ct = default)
    {
        var result = string.IsNullOrWhiteSpace(_settings.ApiKey)
            ? FallbackTextCheck(text)
            : await ModerateTextWithOpenAiAsync(text, ct);

        if (!result.Allowed)
            throw new AdvertModerationException(result.Reason ?? "Contenu texte non autorisé.");
    }

    public async Task EnsureImageAllowedAsync(Stream imageStream, string contentType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new AdvertModerationException(
                "La vérification des photos nécessite OpenAI. Configurez OpenAI:ApiKey sur le serveur.");

        await using var buffer = new MemoryStream();
        await imageStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new ArgumentException("Image vide.");

        var result = await ModerateImageWithOpenAiAsync(buffer.ToArray(), contentType, ct);
        if (!result.Allowed)
            throw new AdvertModerationException(result.Reason ?? "Photo non autorisée.");
    }

    private async Task<AdvertModerationCheckResult> ModerateTextWithOpenAiAsync(string text, CancellationToken ct)
    {
        var client = CreateClient();
        var body = new { model = _settings.ModerationModel, input = text };

        using var response = await client.PostAsync(
            "https://api.openai.com/v1/moderations",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = doc.RootElement.GetProperty("results")[0];
        if (!result.GetProperty("flagged").GetBoolean())
            return Allowed();

        var violations = new List<string>();
        if (result.TryGetProperty("categories", out var categories))
        {
            foreach (var category in SexualModerationCategories)
            {
                if (categories.TryGetProperty(category, out var flagged) && flagged.GetBoolean())
                    violations.Add("sexual_content");
            }
        }

        if (violations.Count == 0 && result.TryGetProperty("categories", out categories))
        {
            foreach (var prop in categories.EnumerateObject())
            {
                if (prop.Value.GetBoolean())
                {
                    violations.Add("policy_violation");
                    break;
                }
            }
        }

        logger.LogWarning("Advert text blocked by OpenAI moderation: {Violations}", string.Join(", ", violations));
        return Blocked(
            "Cette annonce contient un contenu sexuel ou adulte non autorisé sur Kinshout.",
            violations.Count > 0 ? violations : ["policy_violation"]);
    }

    private async Task<AdvertModerationCheckResult> ModerateImageWithOpenAiAsync(
        byte[] imageBytes,
        string contentType,
        CancellationToken ct)
    {
        var client = CreateClient();
        var dataUrl = ToDataUrl(imageBytes, contentType);
        var prompt = """
            Tu es la modération Kinshout (petites annonces à Kinshasa).
            Analyse cette photo et réponds UNIQUEMENT en JSON:
            {
              "allowed": true/false,
              "violations": ["sexual_content", "non_genuine_image"],
              "reason": "explication courte en français pour l'utilisateur"
            }

            Refuser (allowed=false) si:
            - contenu sexuel, érotique, nudité ou suggestif pour adultes (violations: sexual_content)
            - photo stock / banque d'images (Unsplash, Shutterstock, Getty, iStock, Adobe Stock, etc.)
            - photo professionnelle d'agence immobilière ou catalogue produit téléchargée depuis Internet
            - filigrane, logo de banque d'images, capture d'écran de site web (violations: non_genuine_image)
            - image promotionnelle générique manifestement non prise par le vendeur

            Accepter uniquement des photos authentiques prises par l'utilisateur (souvent au téléphone):
            objet, véhicule, logement, pièce avec éclairage imparfait, environnement réel.

            En cas de doute sur l'authenticité ou le contenu, refuse.
            """;

        var body = new
        {
            model = _settings.VisionModel,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            },
            temperature = 0.1,
            response_format = new { type = "json_object" },
        };

        using var response = await client.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var json = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        var parsed = JsonSerializer.Deserialize<VisionModerationResponse>(json, JsonOptions)
            ?? new VisionModerationResponse(false, ["non_genuine_image"], "Photo non autorisée.");

        if (parsed.Allowed)
            return Allowed();

        var reason = parsed.Reason ?? BuildDefaultImageBlockReason(parsed.Violations);
        logger.LogWarning(
            "Advert image blocked: {Reason} ({Violations})",
            reason,
            string.Join(", ", parsed.Violations));

        return Blocked(reason, parsed.Violations.Count > 0 ? parsed.Violations : ["non_genuine_image"]);
    }

    private static AdvertModerationCheckResult FallbackTextCheck(string text)
    {
        var normalized = text.ToLowerInvariant();
        foreach (var pattern in TextFallbackBlockPatterns)
        {
            if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return Blocked(
                    "Cette annonce contient un contenu sexuel ou adulte non autorisé sur Kinshout.",
                    ["sexual_content"]);
            }
        }

        return Allowed();
    }

    private static string BuildDefaultImageBlockReason(IReadOnlyList<string> violations)
    {
        if (violations.Contains("sexual_content"))
            return "Cette photo contient un contenu sexuel ou adulte non autorisé.";

        return "Seules vos propres photos sont autorisées. Les images téléchargées depuis Internet sont interdites.";
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("OpenAI");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        return client;
    }

    private static string ToDataUrl(byte[] bytes, string contentType)
    {
        var mime = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType.Split(';')[0].Trim();
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static AdvertModerationCheckResult Allowed() => new(true, null, []);

    private static AdvertModerationCheckResult Blocked(string reason, IReadOnlyList<string> violations) =>
        new(false, reason, violations);

    private sealed record VisionModerationResponse(
        bool Allowed,
        List<string> Violations,
        string? Reason);
}
