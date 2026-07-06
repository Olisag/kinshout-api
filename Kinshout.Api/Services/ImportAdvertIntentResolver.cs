using Kinshout.Api.Dtos;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

/// <summary>Resolves offre vs demande for imported external adverts.</summary>
public static class ImportAdvertIntentResolver
{
    public static AdvertIntent Resolve(ImportExternalAdvertDto item)
    {
        var text = BuildSignalText(item);
        var demandScore = ScoreDemand(text);
        var offerScore = ScoreOffer(text);

        if (demandScore > offerScore && demandScore > 0)
            return AdvertIntent.Demande;
        if (offerScore > demandScore && offerScore > 0)
            return AdvertIntent.Offre;

        if (TryResolveFromExplicitTokens(item, out var explicitIntent))
            return explicitIntent;

        if (TryResolveFromModality(item.Modality, out explicitIntent))
            return explicitIntent;

        if (TryResolveFromSubcategory(item.Subcategory, text, out explicitIntent))
            return explicitIntent;

        return AdvertIntent.Offre;
    }

    private static string BuildSignalText(ImportExternalAdvertDto item)
    {
        var parts = new List<string?>
        {
            item.Title,
            item.Description,
            item.Ai?.Summary,
            item.Subcategory,
            item.Category,
            item.Modality,
        };

        if (item.Ai?.Intent is not null)
            parts.AddRange(item.Ai.Intent);

        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static bool TryResolveFromExplicitTokens(ImportExternalAdvertDto item, out AdvertIntent intent)
    {
        intent = AdvertIntent.Offre;
        if (item.Ai?.Intent is not { Count: > 0 })
            return false;

        foreach (var raw in item.Ai.Intent)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var token = raw.Trim().ToLowerInvariant();
            if (token is "demande" or "wanted" or "buying" or "search" or "recherche" or "cherche")
            {
                intent = AdvertIntent.Demande;
                return true;
            }

            if (token is "offre" or "offer" or "selling" or "sell")
            {
                intent = AdvertIntent.Offre;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveFromModality(string? modality, out AdvertIntent intent)
    {
        intent = AdvertIntent.Offre;
        if (string.IsNullOrWhiteSpace(modality))
            return false;

        var normalized = modality.Trim().ToLowerInvariant();
        if (normalized is "demande" or "wanted" or "search" or "recherche" or "cherche" or "buying")
        {
            intent = AdvertIntent.Demande;
            return true;
        }

        if (normalized is "offre" or "offer" or "rent" or "sale" or "location" or "vente" or "sell" or "selling")
        {
            intent = AdvertIntent.Offre;
            return true;
        }

        return false;
    }

    private static bool TryResolveFromSubcategory(string? subcategory, string text, out AdvertIntent intent)
    {
        intent = AdvertIntent.Offre;
        if (string.IsNullOrWhiteSpace(subcategory))
            return false;

        var sub = subcategory.Trim().ToLowerInvariant();
        if (sub.Contains("recherche", StringComparison.Ordinal) || sub.Contains("demande", StringComparison.Ordinal))
        {
            intent = AdvertIntent.Demande;
            return true;
        }

        if (sub is "offre_emploi")
        {
            intent = ScoreDemand(text) >= ScoreOffer(text) ? AdvertIntent.Demande : AdvertIntent.Offre;
            return true;
        }

        if (sub.Contains("offre", StringComparison.Ordinal)
            || sub.Contains("_a_louer", StringComparison.Ordinal)
            || sub.Contains("_a_vendre", StringComparison.Ordinal))
        {
            intent = AdvertIntent.Offre;
            return true;
        }

        return false;
    }

    private static int ScoreDemand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var norm = text.ToLowerInvariant();
        return DemandPhrases.Count(phrase => norm.Contains(phrase, StringComparison.Ordinal));
    }

    private static int ScoreOffer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var norm = text.ToLowerInvariant();
        return OfferPhrases.Count(phrase => norm.Contains(phrase, StringComparison.Ordinal));
    }

    private static readonly string[] DemandPhrases =
    [
        "cherche ", " cherche", "recherche", "besoin de", "besoin d'", "je veux", "nous cherchons",
        "looking for", "want to buy", "want to rent", "need a", "need an", "wanted",
        "koluka", "kolinga", "nazali koluka", "nazali kolinga", "nalingi",
        "candidature", "candidat", "cv ", " cv", "cherche un emploi", "cherche une emploi",
        "demande d'emploi", "demande d emploi",
    ];

    private static readonly string[] OfferPhrases =
    [
        "à vendre", "a vendre", "à louer", "a louer", "vends ", " vends", "vendre", "vend ", " vend",
        "disponible", "propose", "offre", "loue ", " loue", "louer un", "louer une",
        "for sale", "for rent", "selling", "sell ", " sell", "available", "rent out", "to rent",
        "koteka", "kotia", "na koteka", "kotisa", "ezali na koteka",
        "recrute", "recrutement", "hiring", "we are hiring", "poste à pourvoir", "poste a pourvoir",
        "offre d'emploi", "offre d emploi", "embauche",
    ];
}
