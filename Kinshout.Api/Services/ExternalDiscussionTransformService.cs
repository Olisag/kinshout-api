using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public record ExternalDiscussionTransformResult(
    string Title,
    string Body,
    bool UsedFallback,
    bool SameStory = false,
    bool Blocked = false);

public interface IExternalDiscussionTransformService
{
    Task<ExternalDiscussionTransformResult> TransformAsync(
        string rawText,
        string? originalAuthor,
        string? platformName,
        CancellationToken ct = default,
        IReadOnlyList<string>? recentStories = null);
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
        CancellationToken ct = default,
        IReadOnlyList<string>? recentStories = null)
    {
        var raw = rawText.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new ExternalDiscussionTransformResult(
                "Sujet Kinshasa",
                "Discussion partagée depuis une source externe.",
                true);

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return DiscussionSourceText.ApplyTitle(FallbackTransform(raw), raw);

        var stories = (recentStories ?? [])
            .Select(story => story.Trim())
            .Where(story => story.Length > 0)
            .Take(ImportedStoryMatch.StoriesForModel)
            .ToList();
        var author = string.IsNullOrWhiteSpace(originalAuthor) ? "inconnu" : originalAuthor.Trim();
        var platform = string.IsNullOrWhiteSpace(platformName) ? "réseau social" : platformName.Trim();
        var sameStoryField = stories.Count == 0
            ? ""
            : ",\n              \"sameStory\": null";
        var blockedField = ",\n              \"blocked\": false";
        var storyBlock = stories.Count == 0
            ? ""
            : "\nPublications déjà sur Kinoiserie:\n"
                + string.Join("\n", stories.Select((story, index) => $"{index + 1}. {OneLine(story)}"))
                + "\n";
        var sameStoryRules = stories.Count == 0
            ? ""
            : """
            - sameStory est le numéro de la publication qui raconte le même fait, ou null
            - Même personne, même affirmation et même lieu: sameStory, même si les mots diffèrent
            - Un nom seul ne suffit pas. Deux histoires différentes sur la même personne restent null
            - Un démenti, la personne qui s'exprime, un fait nouveau, une autre séance ou une autre date reste null
            - En cas de doute, null

            """;
        var prompt = $$"""
            Prépare une discussion Kinoiserie à partir d'une publication importée. Le titre et le corps doivent rester fidèles au texte source.

            Réponds UNIQUEMENT en JSON:
            {
              "title": "le sujet réel, max 70 caractères, sans emoji ni hashtag. Une question seulement si le texte la porte vraiment.",
              "body": "les faits du texte source, en français. Une question courte et précise est permise seulement si le texte la rend possible."{{sameStoryField}}{{blockedField}}
            }

            Règles:
            - title nomme ce dont parle le texte (personne, chanson, objet, lieu, événement). Un titre déclaratif suffit
            - body reprend les détails présents: prix, lieu, date, description
            - Une question est facultative. Elle doit porter sur un fait présent dans le texte et rester grammaticalement une vraie question
            - Interdit comme formule automatique: « qu'en pensez-vous », « votre avis », « êtes-vous d'accord », « et vous », « quelqu'un connaît »
            - N'invente aucun fait. Ne dis pas avoir vu la vidéo si seul le texte est fourni
            - Garde les numéros, e-mails et URL du texte
            - Si le texte commence par « Titre: », ce titre est le sujet. Ne le remplace pas par un pied de page (partenariat, dons, abonnement)
            - Pas d'emoji, de hashtag, ni de majuscules agressives. Français naturel. Lingala seulement si le texte source est en lingala
            - blocked est true si le texte est politique, pornographique, haineux, tribal, raciste ou une haine religieuse, en français ou en lingala
            - Un clip de musique, une blague, un fait divers ou la vie à Kinshasa reste blocked false
            - Un engagement élevé ou un compte connu ne change pas blocked
            {{sameStoryRules}}
            Auteur source: {{author}}
            Plateforme: {{platform}}
            {{storyBlock}}
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

            var sameStory = ImportedStoryMatch.TryReadSameStory(root, stories.Count);
            var blocked = root.TryGetProperty("blocked", out var blockedNode)
                && blockedNode.ValueKind == JsonValueKind.True;
            return DiscussionSourceText.ApplyTitle(
                new ExternalDiscussionTransformResult(title, body, false, sameStory, blocked),
                raw);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External discussion AI transform failed; using fallback.");
            return DiscussionSourceText.ApplyTitle(FallbackTransform(raw), raw);
        }
    }

    private static string OneLine(string story) =>
        story.Replace("\"", "'", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

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
                new { role = "system", content = "Tu prépares une discussion Kinoiserie. Tu gardes les faits, les liens et les contacts. Une question n'est ajoutée que si le texte source la rend possible. Pas de formule toute faite. Réponds en JSON uniquement." },
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
        value = DiscussionStarterEditorial.Title(value.Trim(), source);
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
        var value = body.Trim();
        if (!SharesDistinctiveToken(value, source))
            value = SourceSentences(source);

        value = DiscussionStarterEditorial.Body(value, source);
        if (string.IsNullOrWhiteSpace(value))
            value = DiscussionStarterEditorial.Body(SourceSentences(source), source);

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
