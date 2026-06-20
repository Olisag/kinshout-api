using System.IO.Compression;
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

    private static readonly Dictionary<string, string> ModerationCategoryViolations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sexual"] = "sexual_content",
        ["sexual/minors"] = "sexual_content",
        ["hate"] = "hate_content",
        ["hate/threatening"] = "hate_content",
        ["harassment"] = "harassment",
        ["harassment/threatening"] = "harassment",
        ["violence"] = "violence",
        ["violence/graphic"] = "violence",
    };

    private static readonly string[] TextFallbackBlockPatterns =
    [
        @"\b(porno|pornograph|sexe\s+payant|escort|nud(?:e|ité)|onlyfans|plan\s+cul)\b",
        @"\b(prostitu|massage\s+érotique|massage\s+erotique)\b",
        @"\b(haine\s+racial(?:e|)|supremac|génocide|genocide|nazi(?:sme)?|antisémit|antisemit)\b",
        @"\b(harc[eè]lement\s+(sexuel|racial)|insulte\s+racial)\b",
    ];

    private readonly OpenAiSettings _settings = options.Value;

    public async Task EnsureTextAllowedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var result = string.IsNullOrWhiteSpace(_settings.ApiKey)
            ? FallbackTextCheck(text)
            : await ModerateTextWithOpenAiAsync(text, ct);

        if (!result.Allowed)
            throw new AdvertModerationException(result.Reason ?? "Contenu texte non autorisé.");
    }

    public async Task EnsureImageAllowedAsync(Stream imageStream, string contentType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            logger.LogWarning(
                "OpenAI ApiKey is not configured; skipping image moderation for this upload.");
            return;
        }

        await using var buffer = new MemoryStream();
        await imageStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new ArgumentException("Image vide.");

        var result = await ModerateImageWithOpenAiAsync(buffer.ToArray(), contentType, ct);
        if (!result.Allowed)
            throw new AdvertModerationException(result.Reason ?? "Photo non autorisée.");
    }

    public async Task EnsureDocumentAllowedAsync(
        Stream fileStream,
        string contentType,
        string fileName,
        CancellationToken ct = default)
    {
        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new ArgumentException("Fichier vide.");

        var extension = Path.GetExtension(fileName);
        var extracted = ExtractDocumentText(buffer.ToArray(), extension);
        var textToModerate = string.IsNullOrWhiteSpace(extracted)
            ? fileName
            : $"{fileName}\n{extracted}";

        if (textToModerate.Length > 8000)
            textToModerate = textToModerate[..8000];

        await EnsureTextAllowedAsync(textToModerate, ct);
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
            foreach (var category in ModerationCategoryViolations.Keys)
            {
                if (categories.TryGetProperty(category, out var flagged)
                    && flagged.GetBoolean()
                    && ModerationCategoryViolations.TryGetValue(category, out var violation))
                {
                    violations.Add(violation);
                }
            }
        }

        if (violations.Count == 0)
            violations.Add("policy_violation");

        violations = violations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        logger.LogWarning("Text blocked by OpenAI moderation: {Violations}", string.Join(", ", violations));
        return Blocked(BuildTextBlockReason(violations), violations);
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
              "violations": ["sexual_content", "hate_content", "non_genuine_image"],
              "reason": "explication courte en français pour l'utilisateur"
            }

            Refuser (allowed=false) si:
            - contenu sexuel, érotique, nudité ou suggestif pour adultes (violations: sexual_content)
            - symboles haineux, propos ou images discriminatoires, racistes ou offensants (violations: hate_content)
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
            "Image blocked: {Reason} ({Violations})",
            reason,
            string.Join(", ", parsed.Violations));

        return Blocked(reason, parsed.Violations.Count > 0 ? parsed.Violations : ["non_genuine_image"]);
    }

    private static AdvertModerationCheckResult FallbackTextCheck(string text)
    {
        var normalized = text.ToLowerInvariant();
        foreach (var pattern in TextFallbackBlockPatterns)
        {
            if (!Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            var isHatePattern = pattern.Contains("haine", StringComparison.OrdinalIgnoreCase)
                || pattern.Contains("harc", StringComparison.OrdinalIgnoreCase)
                || pattern.Contains("supremac", StringComparison.OrdinalIgnoreCase)
                || pattern.Contains("nazi", StringComparison.OrdinalIgnoreCase);
            var violations = new List<string> { isHatePattern ? "hate_content" : "sexual_content" };

            return Blocked(BuildTextBlockReason(violations), violations);
        }

        return Allowed();
    }

    private static string BuildTextBlockReason(IReadOnlyList<string> violations)
    {
        if (violations.Contains("sexual_content"))
            return "Ce contenu contient un texte sexuel ou adulte non autorisé sur Kinshout.";

        if (violations.Contains("hate_content"))
            return "Ce contenu contient des propos discriminatoires ou haineux non autorisés sur Kinshout.";

        if (violations.Contains("harassment"))
            return "Ce contenu contient du harcèlement non autorisé sur Kinshout.";

        if (violations.Contains("violence"))
            return "Ce contenu contient de la violence non autorisée sur Kinshout.";

        return "Ce contenu ne respecte pas les règles de Kinshout.";
    }

    private static string BuildDefaultImageBlockReason(IReadOnlyList<string> violations)
    {
        if (violations.Contains("sexual_content"))
            return "Cette photo contient un contenu sexuel ou adulte non autorisé.";

        if (violations.Contains("hate_content"))
            return "Cette photo contient un contenu discriminatoire ou haineux non autorisé.";

        return "Seules vos propres photos sont autorisées. Les images téléchargées depuis Internet sont interdites.";
    }

    private static string ExtractDocumentText(byte[] bytes, string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => ExtractPdfText(bytes),
            ".docx" => ExtractDocxText(bytes),
            ".doc" => ExtractBinaryDocumentText(bytes),
            _ => string.Empty,
        };
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        var content = Encoding.Latin1.GetString(bytes);
        var parts = new List<string>();

        foreach (Match match in Regex.Matches(content, @"\((?:\\.|[^\\)])*\)\s*Tj"))
            parts.Add(UnescapePdfText(match.Groups[1].Value));

        foreach (Match match in Regex.Matches(content, @"\[((?:\\.|[^\]])*)\]\s*TJ"))
        {
            foreach (Match textMatch in Regex.Matches(match.Groups[1].Value, @"\((?:\\.|[^\\)])*\)"))
                parts.Add(UnescapePdfText(textMatch.Groups[0].Value.Trim('(', ')')));
        }

        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string UnescapePdfText(string value) =>
        value
            .Replace("\\(", "(", StringComparison.Ordinal)
            .Replace("\\)", ")", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\r", " ", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal);

    private static string ExtractDocxText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null)
            return string.Empty;

        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        return Regex.Replace(xml, "<[^>]+>", " ");
    }

    private static string ExtractBinaryDocumentText(byte[] bytes)
    {
        var content = Encoding.Latin1.GetString(bytes);
        var matches = Regex.Matches(content, @"[\x20-\x7E]{4,}");
        return string.Join(' ', matches.Select(m => m.Value));
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
