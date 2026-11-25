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
            return DiscussionSourceText.ApplyTitle(FallbackTransform(raw), raw);

        var author = string.IsNullOrWhiteSpace(originalAuthor) ? "inconnu" : originalAuthor.Trim();
        var platform = string.IsNullOrWhiteSpace(platformName) ? "réseau social" : platformName.Trim();
        var prompt = $$"""
            Nettoie une publication pour Kinshout. Le titre et le corps doivent dire la même chose que le texte source.

            Réponds UNIQUEMENT en JSON:
            {
              "title": "le sujet réel, max 70 caractères, sans point d'interrogation, sans emoji ni hashtag",
              "body": "les faits du texte source, en français. Pas une nouvelle histoire."
            }

            Règles:
            - title nomme ce dont parle le texte (personne, chanson, objet, lieu, événement). Pas un autre sujet
            - body reprend les détails présents: prix, lieu, date, description. Une phrase déclarative suffit
            - N'ajoute pas de question. Pas de « qu'en pensez-vous », « quelqu'un connaît », ni d'invitation absente du texte
            - Garde les numéros, e-mails et URL du texte, chacun sur sa ligne en fin de body s'ils ne sont pas déjà dans une phrase
            - N'invente aucun fait
            - Si le texte commence par « Titre: », ce titre est le sujet. Ne le remplace pas par un pied de page (partenariat, dons, abonnement)
            - Pas d'emoji, de hashtag, ni de majuscules agressives

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
                return DiscussionSourceText.ApplyTitle(FallbackTransform(raw), raw);

            return DiscussionSourceText.ApplyTitle(new ExternalDiscussionTransformResult(title, body, false), raw);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External discussion AI transform failed; using fallback.");
            return DiscussionSourceText.ApplyTitle(FallbackTransform(raw), raw);
        }
    }

    internal static ExternalDiscussionTransformResult FallbackTransform(string raw)
    {
        var prose = DiscussionSourceText.WithoutMarkedTitle(raw);
        var cleaned = CleanRawText(prose);
        var title = BuildFallbackTitle(cleaned);
        var body = BuildFallbackBody(prose);
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
                new { role = "system", content = "Tu nettoies un post pour Kinshout. Tu gardes le sujet, les faits, les liens et les contacts. Tu n'ajoutes ni question ni fait absent du texte. Réponds en JSON uniquement." },
                new { role = "user", content = prompt },
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
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
    }

    private static string CleanTitle(string? title, string rawFallback)
    {
        var source = SourceProse(rawFallback);
        var value = string.IsNullOrWhiteSpace(title) ? BuildFallbackTitle(CleanRawText(source)) : CleanRawText(title);
        value = value.Trim().TrimEnd('?', '.', '!', ' ');
        if (!DiscussionSourceText.TryReadMarkedTitle(rawFallback, out _)
            && !SharesDistinctiveToken(value, source))
        {
            value = BuildFallbackTitle(CleanRawText(source));
        }

        return value.Length <= 70 ? value : value[..67] + "...";
    }

    private static string CleanBody(string? body, string rawFallback)
    {
        if (string.IsNullOrWhiteSpace(body))
            return BuildFallbackBody(rawFallback);

        return PrepareBody(body, rawFallback);
    }

    /// <summary>
    /// Drops a question the source did not ask, and puts back links and contacts the rewrite left out.
    /// A body about a different subject is replaced by the source wording.
    /// </summary>
    internal static string PrepareBody(string body, string raw)
    {
        var source = SourceProse(raw);
        var value = DropAddedQuestions(body.Trim(), source);
        if (!SharesDistinctiveToken(value, source))
            value = SourceSentences(source);

        value = AppendMissingUrls(value, raw);
        value = AppendMissingContacts(value, source);
        return value.Length <= 1200 ? value : TruncatePreservingUrl(value, 1200);
    }

    private static string BuildFallbackTitle(string cleaned)
    {
        var sentence = SplitSentences(cleaned).FirstOrDefault(piece => piece.Length > 10) ?? cleaned;
        sentence = HashtagRegex().Replace(sentence, "").Trim();
        sentence = sentence.Trim().TrimEnd('?', '.', '!', ' ');
        if (sentence.Length > 70)
            sentence = sentence[..67] + "...";
        return string.IsNullOrWhiteSpace(sentence) ? "Sujet Kinshasa" : sentence;
    }

    private static string BuildFallbackBody(string raw) => PrepareBody(SourceSentences(SourceProse(raw)), raw);

    private static string SourceProse(string raw) => DiscussionSourceText.WithoutMarkedTitle(raw);

    private static string SourceSentences(string source)
    {
        var cleaned = CleanRawText(source);
        var sentences = SplitSentences(cleaned).Take(4).ToList();
        var context = string.Join(" ", sentences).Trim();
        if (context.Length > 800)
            context = context[..797] + "...";

        return string.IsNullOrWhiteSpace(context)
            ? "Discussion partagée depuis une source externe."
            : context;
    }

    private static string DropAddedQuestions(string body, string source)
    {
        if (source.Contains('?', StringComparison.Ordinal))
            return body;

        var value = body;
        while (value.TrimEnd().EndsWith('?'))
        {
            var cut = LastBreak(value);
            var next = cut < 0 ? "" : value[..cut].Trim();
            if (string.IsNullOrWhiteSpace(next))
                return "";

            value = next;
        }

        return value;
    }

    private static int LastBreak(string value)
    {
        var trimmed = value.TrimEnd();
        for (var i = trimmed.Length - 2; i >= 0; i--)
        {
            if (trimmed[i] is '.' or '!' or '\n')
                return i + 1;
        }

        return -1;
    }

    private static string AppendMissingUrls(string body, string raw)
    {
        var value = body.Trim();
        foreach (Match match in UrlRegex().Matches(raw))
        {
            var url = match.Value.TrimEnd(')', ']', '.', ',', ';', '"', '\'');
            if (!KeepUrl(url))
                continue;

            var stored = IsYouTubeVideoUrl(url) ? NormalizeYouTubeUrl(url) : url;
            if (value.Contains(stored, StringComparison.OrdinalIgnoreCase))
                continue;

            value = string.IsNullOrWhiteSpace(value) ? stored : $"{value.TrimEnd()}\n\n{stored}";
        }

        return value;
    }

    private static bool KeepUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (IsYouTubeVideoUrl(url))
            return true;

        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.AbsolutePath.Length > 1;
    }

    private static string AppendMissingContacts(string body, string source)
    {
        var value = body.Trim();
        foreach (Match match in EmailRegex().Matches(source))
            value = AppendIfMissing(value, match.Value);

        foreach (Match match in PhoneRegex().Matches(source))
        {
            var phone = match.Value.Trim();
            if (phone.Count(char.IsDigit) < 8)
                continue;
            if (DigitsOf(value).Contains(DigitsOf(phone), StringComparison.Ordinal))
                continue;

            value = string.IsNullOrWhiteSpace(value) ? phone : $"{value.TrimEnd()}\n\n{phone}";
        }

        return value;
    }

    private static string AppendIfMissing(string body, string token)
    {
        if (body.Contains(token, StringComparison.OrdinalIgnoreCase))
            return body;

        return string.IsNullOrWhiteSpace(body) ? token : $"{body.TrimEnd()}\n\n{token}";
    }

    private static string DigitsOf(string value)
    {
        var digits = new char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
                digits[count++] = ch;
        }

        return new string(digits, 0, count);
    }

    private static bool SharesDistinctiveToken(string text, string source)
    {
        var found = false;
        foreach (var token in DistinctiveTokens(source))
        {
            found = true;
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return !found;
    }

    private static IEnumerable<string> DistinctiveTokens(string source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in source.Split([' ', '\n', '\r', '\t', ',', '.', '!', '?', ':', ';', '"', '\'', '’', '(', ')', '[', ']', '#', '/', '-', '—'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length < 5 && !token.Any(char.IsDigit))
                continue;
            if (StopWords.Contains(token))
                continue;
            if (token.All(ch => !char.IsLetterOrDigit(ch)))
                continue;
            if (seen.Add(token))
                yield return token;
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "kinshasa", "kinois", "kinoise", "congo", "congolais", "congolaise",
        "video", "vidéo", "nouveau", "nouvelle", "officiel", "officielle",
        "discussion", "pensez", "aujourd", "demain", "comme", "cette", "votre",
        "notre", "avec", "pour", "dans", "plus", "tout", "tous", "chez", "donc",
        "alors", "aussi", "bien", "leur", "leurs", "apres", "après", "être",
        "etre", "fait", "faire", "sont", "vous", "nous", "mais", "dont",
    };

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
            .Where(s => s.Length > 10 || s.Any(char.IsDigit));

    [GeneratedRegex(@"#\w+", RegexOptions.IgnoreCase)]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?:\+|00)?\d[\d\s().-]{6,}\d", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneRegex();
}
