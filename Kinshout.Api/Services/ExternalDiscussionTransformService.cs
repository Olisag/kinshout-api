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
            return new ExternalDiscussionTransformResult(
                "Sujet Kinshasa",
                "Discussion partagée depuis une source externe.",
                true);

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return FallbackTransform(raw);

        var author = string.IsNullOrWhiteSpace(originalAuthor) ? "inconnu" : originalAuthor.Trim();
        var platform = string.IsNullOrWhiteSpace(platformName) ? "réseau social" : platformName.Trim();
        var prompt = $$"""
            Transforme une publication sur Kinshasa en sujet de discussion Kinshout (style forum Reddit).

            Réponds UNIQUEMENT en JSON:
            {
              "title": "libellé court du sujet (max 70 caractères, PAS de point d'interrogation, pas d'emoji ni hashtag)",
              "body": "2 à 4 phrases en français (contexte, réaction ou résumé). Une question ouverte est OPTIONNELLE — ne l'ajoute que si elle apporte vraiment quelque chose."
            }

            Règles:
            - title = le sujet seulement (ex: "Retour des Léopards à Kinshasa", "Résultats EXETAT 2026 — Kinshasa Lukunga")
            - body = contexte factuel tiré du texte; ton vivant, style discussion Kinshasa/RDC
            - Ne termine PAS toujours par une question; variez (résumé, constat, réaction). Question seulement parfois.
            - Ne pas inventer de faits absents du texte source
            - Pas de emojis, hashtags, ni MAJUSCULES agressives dans title/body
            - Si le texte source contient une URL YouTube (youtube.com/watch, youtu.be, shorts), conserve-la telle quelle sur sa propre ligne à la fin du body

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
        var body = BuildFallbackBody(raw);
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
            return BuildFallbackBody(rawFallback);

        var value = body.Trim();
        // Preserve YouTube URLs from the raw source when the model dropped them.
        value = EnsureYouTubeUrlPreserved(value, rawFallback);
        return value.Length <= 1200 ? value : TruncatePreservingUrl(value, 1200);
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

    private static string BuildFallbackBody(string raw)
    {
        var youtubeUrl = ExtractYouTubeUrl(raw);
        var cleaned = CleanRawText(raw);
        var sentences = SplitSentences(cleaned).Take(3).ToList();
        var context = string.Join(" ", sentences).Trim();
        if (context.Length > 400)
            context = context[..397] + "...";

        if (string.IsNullOrWhiteSpace(context))
            context = "Discussion partagée depuis une source externe.";

        return string.IsNullOrWhiteSpace(youtubeUrl)
            ? context
            : $"{context}\n\n{youtubeUrl}";
    }

    private static string EnsureYouTubeUrlPreserved(string body, string rawFallback)
    {
        if (ExtractYouTubeUrl(body) is not null)
            return body;

        var youtubeUrl = ExtractYouTubeUrl(rawFallback);
        if (youtubeUrl is null)
            return body;

        return $"{body.TrimEnd()}\n\n{youtubeUrl}";
    }

    private static string? ExtractYouTubeUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (Match match in UrlRegex().Matches(text))
        {
            var url = match.Value.TrimEnd(')', ']', '.', ',', ';', '"', '\'');
            if (IsYouTubeVideoUrl(url))
                return NormalizeYouTubeUrl(url);
        }

        return null;
    }

    private static bool IsYouTubeVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Length > 1;

        if (!host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.AbsolutePath;
        if (path.StartsWith("/watch", StringComparison.OrdinalIgnoreCase)
            && uri.Query.Contains("v=", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string NormalizeYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(id) ? url : $"https://www.youtube.com/watch?v={id}";
        }

        if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/live/", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            return string.IsNullOrWhiteSpace(id) ? url : $"https://www.youtube.com/watch?v={id}";
        }

        var videoId = ExtractQueryValue(uri.Query, "v");
        return string.IsNullOrWhiteSpace(videoId)
            ? url
            : $"https://www.youtube.com/watch?v={videoId}";
    }

    private static string? ExtractQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair[1]))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static string TruncatePreservingUrl(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var youtubeUrl = ExtractYouTubeUrl(value);
        if (youtubeUrl is null)
            return value[..(maxLength - 3)] + "...";

        var budget = maxLength - youtubeUrl.Length - 5;
        if (budget < 40)
            return youtubeUrl;

        var withoutUrl = UrlRegex().Replace(value, "").Trim();
        if (withoutUrl.Length > budget)
            withoutUrl = withoutUrl[..(budget - 3)] + "...";
        return $"{withoutUrl}\n\n{youtubeUrl}";
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
