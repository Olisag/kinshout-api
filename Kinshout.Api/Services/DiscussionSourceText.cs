using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

/// <summary>
/// A YouTube title is the video. The description is often a repeated footer
/// (partnership, contacts). The rewrite must keep the video, not the footer.
/// </summary>
public static partial class DiscussionSourceText
{
    public static string ForTransform(string? title, string body)
    {
        var trimmedBody = body.Trim();
        if (!IsIndependentTitle(title, trimmedBody))
            return trimmedBody;

        return "Titre: " + title!.Trim() + "\n\n" + trimmedBody;
    }

    public static bool IsIndependentTitle(string? title, string body)
    {
        var value = title?.Trim() ?? "";
        if (value.EndsWith("...", StringComparison.Ordinal))
            value = value[..^3].Trim();

        if (value.Length < 12)
            return false;

        return !body.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReadMarkedTitle(string raw, out string title)
    {
        title = "";
        var trimmed = raw.TrimStart();
        const string prefix = "Titre:";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmed[prefix.Length..].TrimStart();
        var lineEnd = rest.IndexOf('\n');
        var line = (lineEnd >= 0 ? rest[..lineEnd] : rest).Trim();
        if (string.IsNullOrWhiteSpace(line))
            return false;

        title = line;
        return true;
    }

    public static string WithoutMarkedTitle(string raw)
    {
        if (!TryReadMarkedTitle(raw, out _))
            return raw;

        var trimmed = raw.TrimStart();
        var lineEnd = trimmed.IndexOf('\n');
        return lineEnd < 0 ? "" : trimmed[(lineEnd + 1)..].Trim();
    }

    public static ExternalDiscussionTransformResult ApplyTitle(ExternalDiscussionTransformResult result, string raw)
    {
        if (!TryReadMarkedTitle(raw, out var sourceTitle))
            return result;

        var title = FormatSourceTitle(sourceTitle);
        return result with { Title = title, Body = EnsureBodyNamesSubject(result.Body, title) };
    }

    public static string FormatSourceTitle(string sourceTitle)
    {
        var value = Collapse(sourceTitle);
        value = UrlRegex().Replace(value, "");
        value = HashtagRegex().Replace(value, "");
        value = Collapse(value);
        if (IsShouting(value))
            value = value.ToLowerInvariant();

        value = StripWelcome(value);
        value = value.Replace(" / ", ", ", StringComparison.Ordinal).Replace("/", ", ");
        value = Collapse(value).Replace(" ,", ",", StringComparison.Ordinal);
        value = value.Trim(' ', ',', '.', '!', '?', ';', '-');
        if (value.Length == 0)
            return "Sujet Kinshasa";

        value = char.ToUpperInvariant(value[0]) + value[1..];
        return value.Length <= 90 ? value : value[..87].TrimEnd() + "...";
    }

    public static string EnsureBodyNamesSubject(string body, string formattedTitle)
    {
        var trimmed = body.Trim();
        if (SharesSubject(trimmed, formattedTitle))
            return trimmed;

        var lead = formattedTitle.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(trimmed) ? lead : $"{lead}. {trimmed}";
    }

    private static bool SharesSubject(string body, string formattedTitle)
    {
        foreach (var token in SubjectTokens(formattedTitle))
        {
            if (body.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> SubjectTokens(string title)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "culte", "notre", "avec", "pasteur", "session", "matin", "soir",
            "jeudi", "mercredi", "dimanche", "lundi", "mardi", "vendredi", "samedi",
            "bienvenue", "vacation", "monde", "mondiale", "officiel",
        };

        foreach (var raw in title.Split([' ', ',', '&', '\'', '’', '.', '-', '—'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length >= 4 && token.Any(char.IsDigit) && token.Any(c => c is '.' or '/' or '-'))
            {
                yield return token;
                continue;
            }

            if (token.Length >= 6 && !skip.Contains(token))
                yield return token;
        }
    }

    private static string StripWelcome(string value)
    {
        foreach (var prefix in WelcomePrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value[prefix.Length..].TrimStart();
        }

        return value;
    }

    private static readonly string[] WelcomePrefixes =
    [
        "bienvenue à notre ",
        "bienvenue a notre ",
        "bienvenue au ",
        "bienvenue à la ",
        "bienvenue a la ",
        "bienvenue à ",
        "bienvenue a ",
        "bienvenue ",
    ];

    private static bool IsShouting(string value)
    {
        var letters = 0;
        var uppers = 0;
        foreach (var ch in value)
        {
            if (!char.IsLetter(ch))
                continue;
            letters++;
            if (char.IsUpper(ch))
                uppers++;
        }

        return letters >= 8 && uppers * 5 >= letters * 3;
    }

    private static string Collapse(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    [GeneratedRegex(@"#\w+", RegexOptions.IgnoreCase)]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
