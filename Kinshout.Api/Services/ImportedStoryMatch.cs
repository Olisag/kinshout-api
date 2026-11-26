using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

/// <summary>
/// A repeated caption is dropped before the rewrite. A differently worded retelling of the same
/// incident is decided by the rewrite call. A name on its own is not a repeat.
/// </summary>
internal static partial class ImportedStoryMatch
{
    public const int LookbackDays = 7;
    public const int NearCopyScan = 80;
    public const int StoriesForModel = 12;

    public static bool IsNearCopy(string incomingTitle, string incomingBody, string existingTitle, string existingBody)
    {
        if (!Shares(
                Tokens(Join(incomingTitle, incomingBody)),
                Tokens(Join(existingTitle, existingBody)),
                minShared: 8,
                minJaccard: 0.6))
            return false;

        return TitlesAgree(Tokens(incomingTitle), Tokens(existingTitle));
    }

    private static bool TitlesAgree(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count < 4 || right.Count < 4)
            return true;

        var shared = left.Count(right.Contains);
        var smaller = Math.Min(left.Count, right.Count);
        if ((double)shared / smaller >= 0.8)
            return true;

        var union = left.Count + right.Count - shared;
        return shared >= 5 && union > 0 && (double)shared / union >= 0.72;
    }

    public static string Blurb(string? title, string? body)
    {
        var text = Join(title, body);
        return text.Length <= 280 ? text : text[..277].TrimEnd() + "...";
    }

    public static bool TryReadSameStory(JsonElement root, int storyCount)
    {
        if (storyCount <= 0 || !root.TryGetProperty("sameStory", out var same))
            return false;

        if (same.ValueKind == JsonValueKind.Number && same.TryGetInt32(out var number))
            return number >= 1 && number <= storyCount;

        if (same.ValueKind == JsonValueKind.String
            && int.TryParse(same.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed >= 1 && parsed <= storyCount;

        return false;
    }

    private static bool Shares(HashSet<string> left, HashSet<string> right, int minShared, double minJaccard)
    {
        if (left.Count < minShared || right.Count < minShared)
            return false;

        var shared = left.Count(right.Contains);
        if (shared < minShared)
            return false;

        var union = left.Count + right.Count - shared;
        return union > 0 && (double)shared / union >= minJaccard;
    }

    private static string Join(string? title, string? body)
    {
        var left = title?.Trim() ?? "";
        var right = body?.Trim() ?? "";
        if (left.Length == 0)
            return right;
        if (right.Length == 0 || left.Equals(right, StringComparison.OrdinalIgnoreCase))
            return left;
        return left + " " + right;
    }

    private static HashSet<string> Tokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var folded = Fold(text);
        foreach (Match match in WordRegex().Matches(folded))
        {
            var token = match.Value;
            if (token.Length < 4 || StopWords.Contains(token))
                continue;
            tokens.Add(token);
        }

        return tokens;
    }

    private static string Fold(string text)
    {
        var form = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var character in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "dans", "avec", "pour", "cette", "plus", "comme", "donc", "aussi", "alors",
        "mais", "leur", "leurs", "vous", "nous", "elle", "elles", "sont", "etre",
        "etait", "fait", "faire", "video", "videos", "cette", "aux", "pas", "sur",
        "par", "que", "qui", "des", "les", "une", "tout", "tous",
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
