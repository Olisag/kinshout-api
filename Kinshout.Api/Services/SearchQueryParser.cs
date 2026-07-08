using System.Text.RegularExpressions;

namespace Kinshout.Api.Services;

public sealed class ParsedSearchQuery
{
    public required string OriginalQuery { get; init; }
    public required string SubjectText { get; init; }
    public string? IntentHint { get; init; }
    public bool MatchedPattern { get; init; }
}

/// <summary>
/// Tier-1 sentence parser: extracts the subject (what user wants) from common FR/EN/Lingala templates
/// before keyword expansion and SQL retrieval.
/// </summary>
public static partial class SearchQueryParser
{
    private static readonly string[] LeadingArticles =
    [
        "un", "une", "des", "du", "de", "le", "la", "les", "mon", "ma", "mes", "ton", "ta", "tes", "son", "sa", "ses",
        "a", "an", "the", "my", "your", "some", "moko", "moko ya",
    ];

    public static ParsedSearchQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ParsedSearchQuery { OriginalQuery = "", SubjectText = "", IntentHint = null, MatchedPattern = false };

        var original = query.Trim();
        var normalized = SearchTextNormalizer.Normalize(original);
        if (normalized.Length < 2)
            return new ParsedSearchQuery { OriginalQuery = original, SubjectText = original, IntentHint = null, MatchedPattern = false };

        foreach (var (pattern, intent) in DemandPatterns)
        {
            var match = pattern.Match(normalized);
            if (!match.Success)
                continue;

            var subject = CleanSubject(match.Groups["subject"].Value);
            if (subject.Length >= 2)
            {
                return new ParsedSearchQuery
                {
                    OriginalQuery = original,
                    SubjectText = subject,
                    IntentHint = intent,
                    MatchedPattern = true,
                };
            }
        }

        foreach (var (pattern, intent) in OfferPatterns)
        {
            var match = pattern.Match(normalized);
            if (!match.Success)
                continue;

            var subject = CleanSubject(match.Groups["subject"].Value);
            if (subject.Length >= 2)
            {
                return new ParsedSearchQuery
                {
                    OriginalQuery = original,
                    SubjectText = subject,
                    IntentHint = intent,
                    MatchedPattern = true,
                };
            }
        }

        return new ParsedSearchQuery
        {
            OriginalQuery = original,
            SubjectText = SearchSpellingNormalizer.CanonicalizeText(normalized),
            IntentHint = null,
            MatchedPattern = false,
        };
    }

    private static string CleanSubject(string subject)
    {
        var cleaned = subject.Trim();
        cleaned = LeadingArticlePrefix().Replace(cleaned, "");
        cleaned = TrailingPreposition().Replace(cleaned, "");
        return SearchSpellingNormalizer.CanonicalizeText(cleaned.Trim());
    }

    private static (Regex Pattern, string Intent)[] DemandPatterns =>
    [
        (FrenchJeCherche(), SearchIntentHelper.Demande),
        (FrenchJeVeux(), SearchIntentHelper.Demande),
        (FrenchRecherche(), SearchIntentHelper.Demande),
        (FrenchBesoinDe(), SearchIntentHelper.Demande),
        (EnglishLookingFor(), SearchIntentHelper.Demande),
        (EnglishINeed(), SearchIntentHelper.Demande),
        (EnglishIWant(), SearchIntentHelper.Demande),
        (LingalaNalingi(), SearchIntentHelper.Demande),
        (LingalaNazaliKoluka(), SearchIntentHelper.Demande),
        (LingalaNazaliKolinga(), SearchIntentHelper.Demande),
    ];

    private static (Regex Pattern, string Intent)[] OfferPatterns =>
    [
        (FrenchJeVends(), SearchIntentHelper.Offre),
        (FrenchJePropose(), SearchIntentHelper.Offre),
        (FrenchAVendre(), SearchIntentHelper.Offre),
        (FrenchVenteDe(), SearchIntentHelper.Offre),
        (FrenchVente(), SearchIntentHelper.Offre),
        (EnglishSelling(), SearchIntentHelper.Offre),
        (EnglishForSale(), SearchIntentHelper.Offre),
        (LingalaNazaliKoteka(), SearchIntentHelper.Offre),
        (LingalaKoteka(), SearchIntentHelper.Offre),
    ];

    [GeneratedRegex(@"^je cherche\s+(?:(?:un|une|des|le|la|les|mon|ma|mes)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchJeCherche();

    [GeneratedRegex(@"^je veux\s+(?:(?:un|une|des|le|la|les|mon|ma|mes)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchJeVeux();

    [GeneratedRegex(@"^(?:je\s+)?recherche\s+(?:(?:un|une|des|le|la|les|mon|ma|mes)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchRecherche();

    [GeneratedRegex(@"^(?:j ai|jai)\s+besoin\s+(?:de|d)?\s*(?:(?:un|une|des|le|la|les)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchBesoinDe();

    [GeneratedRegex(@"^looking for\s+(?:(?:a|an|the|my|some)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishLookingFor();

    [GeneratedRegex(@"^i need\s+(?:(?:a|an|the|my|some)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishINeed();

    [GeneratedRegex(@"^i want\s+(?:(?:a|an|the|my|some)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishIWant();

    [GeneratedRegex(@"^nalingi\s+(?:(?:moko|moko ya)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LingalaNalingi();

    [GeneratedRegex(@"^nazali koluka\s+(?:(?:moko|moko ya)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LingalaNazaliKoluka();

    [GeneratedRegex(@"^nazali kolinga\s+(?:(?:moko|moko ya)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LingalaNazaliKolinga();

    [GeneratedRegex(@"^je vends\s+(?:(?:un|une|des|mon|ma|mes)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchJeVends();

    [GeneratedRegex(@"^je propose\s+(?:(?:un|une|des)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchJePropose();

    [GeneratedRegex(@"^(?<subject>.+)\s+a vendre$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchAVendre();

    [GeneratedRegex(@"^vente\s+(?:de|des|du|d)\s+(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchVenteDe();

    [GeneratedRegex(@"^vente\s+(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchVente();

    [GeneratedRegex(@"^selling\s+(?:(?:a|an|my|the)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishSelling();

    [GeneratedRegex(@"^(?<subject>.+)\s+for sale$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishForSale();

    [GeneratedRegex(@"^nazali koteka\s+(?:(?:moko|moko ya)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LingalaNazaliKoteka();

    [GeneratedRegex(@"^koteka\s+(?:(?:moko|moko ya)\s+)?(?<subject>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LingalaKoteka();

    [GeneratedRegex(@"^(?:un|une|des|du|de|le|la|les|mon|ma|mes|ton|ta|tes|son|sa|ses|a|an|the|my|your|some|moko|moko ya)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingArticlePrefix();

    [GeneratedRegex(@"\s+(?:a|à|au|aux|en|dans|sur|pour|de|du|des|in|at|on|for|na|ya)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPreposition();
}
