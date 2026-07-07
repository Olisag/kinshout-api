using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record ExternalDiscussionTransformResult(string Title, string Body, bool UsedFallback);

public interface IExternalDiscussionTransformService
{
    Task<ExternalDiscussionTransformResult> TransformAsync(
        string rawText,
        string? originalAuthor,
        string? platformName,
        CancellationToken ct = default);
}

public partial class ExternalDiscussionTransformService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiSettings> options,
    ILogger<ExternalDiscussionTransformService> logger) : IExternalDiscussionTransformService
{
    private readonly OpenAiSettings _settings = options.Value;

    public async Task<ExternalDiscussionTransformResult> TransformAsync(
        string rawText,
        string? originalAuthor,
        string? platformName,
        CancellationToken ct = default)
    {
        var raw = rawText.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new ExternalDiscussionTransformResult("Sujet Kinshasa", "Qu'en pensez-vous ?", true);

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return FallbackTransform(raw);

        var author = string.IsNullOrWhiteSpace(originalAuthor) ? "inconnu" : originalAuthor.Trim();
        var platform = string.IsNullOrWhiteSpace(platformName) ? "réseau social" : platformName.Trim();
        var prompt = $$"""
            Transforme une publication sur Kinshasa en sujet de discussion Kinshout (style forum Reddit).

            Réponds UNIQUEMENT en JSON:
            {
              "title": "libellé court du sujet (max 70 caractères, PAS de point d'interrogation, pas d'emoji ni hashtag)",
              "body": "2 à 3 phrases de contexte neutre en français, puis UNE seule question pertinente en dernière phrase (se termine par ?)"
            }

            Règles:
            - title = le sujet seulement (ex: "Retour des Léopards à Kinshasa", "Résultats EXETAT 2026 — Kinshasa Lukunga")
            - body = contexte factuel tiré du texte + une question ouverte pour lancer la discussion
            - Ne pas inventer de faits absents du texte source
            - Ton neutre, français clair, public Kinshasa/RDC
            - Pas de emojis, hashtags, ni MAJUSCULES agressives dans title/body

            Auteur source: {{author}}
            Plateforme: {{platform}}

            Texte source:
            {{raw.Replace("\"", "\\\"")}}
            """;

        try
        {
            var json = await ChatJsonAsync(prompt, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = CleanTitle(root.TryGetProperty("title", out var t) ? t.GetString() : null, raw);
            var body = CleanBody(root.TryGetProperty("body", out var b) ? b.GetString() : null, raw);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
                return FallbackTransform(raw);

            return new ExternalDiscussionTransformResult(title, body, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External discussion AI transform failed; using fallback.");
            return FallbackTransform(raw);
        }
    }

    internal static ExternalDiscussionTransformResult FallbackTransform(string raw)
    {
        var cleaned = CleanRawText(raw);
        var title = BuildFallbackTitle(cleaned);
        var body = BuildFallbackBody(cleaned);
        return new ExternalDiscussionTransformResult(title, body, true);
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
                new { role = "system", content = "Tu transformes des posts sociaux en sujets de discussion pour Kinshout (Kinshasa, RDC). Réponds en JSON uniquement." },
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

    private static string CleanTitle(string? title, string rawFallback)
    {
        var value = string.IsNullOrWhiteSpace(title) ? BuildFallbackTitle(CleanRawText(rawFallback)) : CleanRawText(title);
        value = value.Trim().TrimEnd('?', '.', '!', ' ');
        return value.Length <= 70 ? value : value[..67] + "...";
    }

    private static string CleanBody(string? body, string rawFallback)
    {
        if (string.IsNullOrWhiteSpace(body))
            return BuildFallbackBody(CleanRawText(rawFallback));

        var value = body.Trim();
        if (!value.EndsWith('?'))
            value += " Qu'en pensez-vous ?";
        return value.Length <= 1200 ? value : value[..1197] + "...";
    }

    private static string BuildFallbackTitle(string cleaned)
    {
        var sentence = SplitSentences(cleaned).FirstOrDefault() ?? cleaned;
        sentence = HashtagRegex().Replace(sentence, "").Trim();
        sentence = sentence.Trim().TrimEnd('?', '.', '!', ' ');
        if (sentence.Length > 70)
            sentence = sentence[..67] + "...";
        return string.IsNullOrWhiteSpace(sentence) ? "Sujet Kinshasa" : sentence;
    }

    private static string BuildFallbackBody(string cleaned)
    {
        var sentences = SplitSentences(cleaned).Take(3).ToList();
        var context = string.Join(" ", sentences).Trim();
        if (context.Length > 400)
            context = context[..397] + "...";

        var question = BuildFallbackQuestion(cleaned.ToLowerInvariant());
        return string.IsNullOrWhiteSpace(context)
            ? question
            : $"{context}\n\n{question}";
    }

    private static string BuildFallbackQuestion(string lower)
    {
        if (lower.Contains("exetat", StringComparison.Ordinal) || lower.Contains("examen d'état", StringComparison.Ordinal))
            return "Les conditions d'examen à Kinshasa vous semblent-elles équitables cette année ?";
        if (lower.Contains("léopard", StringComparison.Ordinal) || lower.Contains("football", StringComparison.Ordinal) || lower.Contains("coupe du monde", StringComparison.Ordinal))
            return "Comment accueillez-vous la fin de ce parcours en Coupe du Monde ?";
        if (lower.Contains("police", StringComparison.Ordinal) || lower.Contains("sécurité", StringComparison.Ordinal))
            return "Comment améliorer la sécurité au quotidien dans votre quartier ?";
        if (lower.Contains("fayulu", StringComparison.Ordinal) || lower.Contains("opposition", StringComparison.Ordinal) || lower.Contains("politique", StringComparison.Ordinal))
            return "Qu'attendez-vous concrètement de cette initiative pour Kinshasa ?";
        if (lower.Contains("dette", StringComparison.Ordinal) || lower.Contains("altercation", StringComparison.Ordinal))
            return "Comment éviter que ce genre de conflit finisse en violence dans nos quartiers ?";
        if (lower.Contains("kinshasa", StringComparison.Ordinal))
            return "Qu'en pensez-vous, vous qui vivez à Kinshasa ?";
        return "Qu'en pensez-vous ?";
    }

    private static string CleanRawText(string text)
    {
        var value = WhitespaceRegex().Replace(text, " ").Trim();
        value = UrlRegex().Replace(value, "").Trim();
        value = HashtagRegex().Replace(value, "").Trim();
        value = EmojiRegex.Replace(value, "").Trim();
        return value;
    }

    private static readonly Regex EmojiRegex = new(
        @"[\u2600-\u27BF]|[\uD800-\uDBFF][\uDC00-\uDFFF]",
        RegexOptions.Compiled);

    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 10);

    [GeneratedRegex(@"#\w+", RegexOptions.IgnoreCase)]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
