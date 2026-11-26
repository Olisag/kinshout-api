using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

/// <summary>
/// Keeps an imported discussion factual. A question stays only when it is a real
/// question about something the source actually says. Generic prompts are removed.
/// </summary>
public static partial class DiscussionStarterEditorial
{
    public static string Title(string title, string source)
    {
        var value = title.Trim();
        if (!value.Contains('?'))
            return value.TrimEnd('.', '!', ' ');

        if (KeepQuestion(value, source))
            return value;

        var without = value.Replace("?", "", StringComparison.Ordinal).Trim().TrimEnd('.', '!', ' ');
        return string.IsNullOrWhiteSpace(without) ? "" : without;
    }

    public static string Body(string body, string source)
    {
        var kept = new List<string>();
        foreach (var sentence in SplitSentences(body))
        {
            if (!sentence.Contains('?'))
            {
                kept.Add(sentence);
                continue;
            }

            if (KeepQuestion(sentence, source))
                kept.Add(sentence);
        }

        return string.Join(" ", kept).Trim();
    }

    public static bool KeepQuestion(string sentence, string source)
    {
        var value = sentence.Trim();
        if (!value.Contains('?') || IsGenericPrompt(value) || !IsRealQuestion(value))
            return false;

        return SharesToken(value, source);
    }

    public static bool IsGenericPrompt(string sentence)
    {
        var folded = Fold(sentence);
        foreach (var prompt in GenericPrompts)
        {
            if (folded.Contains(prompt, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsRealQuestion(string sentence)
    {
        if (QuestionCue().IsMatch(sentence))
            return true;

        return sentence.Contains(" ou ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SharesToken(string sentence, string source)
    {
        var found = false;
        foreach (var token in Tokens(source))
        {
            found = true;
            if (sentence.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return !found;
    }

    private static IEnumerable<string> Tokens(string source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in source.Split([' ', '\n', '\r', '\t', ',', '.', '!', '?', ':', ';', '"', '\'', '’', '(', ')', '[', ']', '#', '/', '-', '—'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length < 5 && !token.Any(char.IsDigit))
                continue;
            if (StopWords.Contains(token))
                continue;
            if (seen.Add(token))
                yield return token;
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '\n'))
                continue;

            var sentence = text[start..(i + 1)].Trim();
            if (sentence.Length > 0)
                yield return sentence;
            start = i + 1;
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static string Fold(string value) =>
        value.Replace('’', '\'').Replace('ê', 'e').Replace('é', 'e').Replace('è', 'e').Replace('î', 'i').Replace('ï', 'i').ToLowerInvariant();

    private static readonly string[] GenericPrompts =
    [
        "qu'en pensez-vous",
        "votre avis",
        "etes-vous d'accord",
        "et vous",
        "quelqu'un connait",
        "quelqu'un connait",
        "hate de lire",
        "bonne base de discussion",
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "kinshasa", "kinois", "congo", "congolais", "video", "vidéo",
        "nouveau", "nouvelle", "quelle", "quelles", "comment", "pourquoi",
    };

    [GeneratedRegex(@"\b(quel|quelle|quels|quelles|comment|pourquoi|qui|où|ou|lequel|laquelle|est-ce)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionCue();
}
