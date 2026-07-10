using System.Globalization;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public sealed record PopularSearchAggregate(string DisplayLabel, int Count, DateTime LastSearchedAt);

/// <summary>
/// Groups raw search stats into phrase-style popular searches with prefix fusion.
/// </summary>
public static class PopularSearchGrouper
{
    private static readonly HashSet<string> PhraseNoiseTokens = new(StringComparer.Ordinal)
    {
        "tre", "tres", "tres", "très",
        "na", "ya",
        "pas", "cher", "chere",
        "a", "à", "au", "aux", "en", "dans", "sur", "pour", "de", "du", "des", "d",
    };

    public static IReadOnlyList<PopularSearchAggregate> Aggregate(IReadOnlyList<SearchQueryStat> rows)
    {
        if (rows.Count == 0)
            return [];

        var groups = new Dictionary<string, PhraseAccumulator>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var phraseKey = SearchQueryHelper.ResolvePhraseKey(row);
            if (string.IsNullOrWhiteSpace(phraseKey))
                continue;

            if (!groups.TryGetValue(phraseKey, out var accumulator))
            {
                accumulator = new PhraseAccumulator(phraseKey);
                groups[phraseKey] = accumulator;
            }

            accumulator.Add(row);
        }

        var merged = MergePhraseGroups(groups);
        return merged
            .Values
            .Select(g => new PopularSearchAggregate(
                g.BuildDisplayLabel(),
                g.Count,
                g.LastSearchedAt))
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.LastSearchedAt)
            .ToList();
    }

    internal static Dictionary<string, PhraseAccumulator> MergePhraseGroups(
        Dictionary<string, PhraseAccumulator> groups)
    {
        var keys = groups.Keys.OrderByDescending(k => k.Length).ToList();
        var absorbed = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, PhraseAccumulator>(StringComparer.Ordinal);

        foreach (var longKey in keys)
        {
            if (absorbed.Contains(longKey))
                continue;

            var merged = groups[longKey].Clone();
            foreach (var shortKey in keys)
            {
                if (shortKey.Length >= longKey.Length || absorbed.Contains(shortKey))
                    continue;

                if (!longKey.StartsWith(shortKey + " ", StringComparison.Ordinal))
                    continue;

                if (!IsQualityPhraseKey(shortKey) || !CanAbsorbPrefix(shortKey, longKey))
                    continue;

                merged.MergeFrom(groups[shortKey]);
                absorbed.Add(shortKey);
            }

            result[longKey] = merged;
        }

        foreach (var key in keys.Where(k => !absorbed.Contains(k) && !result.ContainsKey(k)))
            result[key] = groups[key];

        return result;
    }

    internal static bool IsQualityPhraseKey(string phraseKey)
    {
        if (string.IsNullOrWhiteSpace(phraseKey))
            return false;

        var tokens = phraseKey.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        return tokens.Any(t => t.Length >= 3 && !PhraseNoiseTokens.Contains(t));
    }

    private static bool CanAbsorbPrefix(string shortKey, string longKey)
    {
        if (!IsQualityPhraseKey(shortKey) || !IsQualityPhraseKey(longKey))
            return false;

        var shortTokens = shortKey.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var longTokens = longKey.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return longTokens.Length > shortTokens.Length;
    }

    internal sealed class PhraseAccumulator
    {
        private readonly string _phraseKey;
        private readonly List<string> _displayQueries = [];

        public PhraseAccumulator(string phraseKey) => _phraseKey = phraseKey;

        public int Count { get; private set; }

        public DateTime LastSearchedAt { get; private set; } = DateTime.MinValue;

        public void Add(SearchQueryStat row)
        {
            Count += row.SearchCount;
            if (row.LastSearchedAt > LastSearchedAt)
                LastSearchedAt = row.LastSearchedAt;

            if (!string.IsNullOrWhiteSpace(row.DisplayQuery))
                _displayQueries.Add(row.DisplayQuery);
        }

        public void MergeFrom(PhraseAccumulator other)
        {
            Count += other.Count;
            if (other.LastSearchedAt > LastSearchedAt)
                LastSearchedAt = other.LastSearchedAt;
            _displayQueries.AddRange(other._displayQueries);
        }

        public PhraseAccumulator Clone()
        {
            var clone = new PhraseAccumulator(_phraseKey)
            {
                Count = Count,
                LastSearchedAt = LastSearchedAt,
            };
            clone._displayQueries.AddRange(_displayQueries);
            return clone;
        }

        public string BuildDisplayLabel() => BuildLabelFromVariants(_displayQueries, _phraseKey);
    }

    internal static string BuildLabelFromVariants(IReadOnlyList<string> displayQueries, string phraseKey)
    {
        var keyTokens = phraseKey.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keyTokens.Length == 0)
            return phraseKey;

        var wordVariants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var query in displayQueries)
        {
            foreach (var word in SplitLabelWords(query))
            {
                var normalized = SearchQueryHelper.NormalizePhraseToken(word);
                if (!keyTokens.Contains(normalized, StringComparer.Ordinal))
                    continue;

                wordVariants[normalized] = PreferBetterWord(
                    wordVariants.GetValueOrDefault(normalized),
                    word,
                    normalized);
            }
        }

        var culture = CultureInfo.GetCultureInfo("fr-FR");
        return string.Join(
            ' ',
            keyTokens.Select(token =>
            {
                var word = wordVariants.GetValueOrDefault(token, token);
                if (word.Length + 1 < token.Length)
                    word = token;
                return culture.TextInfo.ToTitleCase(word.ToLowerInvariant());
            }));
    }

    private static IEnumerable<string> SplitLabelWords(string text) =>
        text.Split([' ', '\t', '\n', '\r', ',', ';', '.', '!', '?', '(', ')', '[', ']', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string PreferBetterWord(string? current, string candidate, string keyToken)
    {
        if (string.IsNullOrWhiteSpace(current))
            return candidate;

        var currentScore = LabelWordScore(current, keyToken);
        var candidateScore = LabelWordScore(candidate, keyToken);
        return candidateScore > currentScore ? candidate : current;
    }

    private static int LabelWordScore(string word, string keyToken)
    {
        var normalized = SearchQueryHelper.NormalizePhraseToken(word);
        if (!string.Equals(normalized, keyToken, StringComparison.Ordinal))
            return -100;

        var score = 0;
        if (word.Any(c => c is 'é' or 'è' or 'ê' or 'ë' or 'à' or 'â' or 'ù' or 'û' or 'ô' or 'î' or 'ï' or 'ç'))
            score += 4;
        if (char.IsUpper(word[0]))
            score += 2;
        score += word.Length;
        if (word.Equals(keyToken, StringComparison.OrdinalIgnoreCase))
            score += 3;
        return score;
    }
}
