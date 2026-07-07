using System.Text.RegularExpressions;

namespace Kinshout.ExternalImporter.Providers.Scraping;

internal readonly record struct ListingCategoryResult(
    string Category,
    string? Subcategory,
    string Modality);

internal static partial class ListingCategoryMapper
{
    public static ListingCategoryResult FromMediaCongo(string url, string? title, string? description, string fallbackCategory)
    {
        var rubrique = ExtractMediaCongoRubrique(url);
        if (!string.IsNullOrWhiteSpace(rubrique))
            return MapRubrique(rubrique, title, description);

        return InferFromText(title, description, fallbackCategory);
    }

    public static ListingCategoryResult InferFromText(string? title, string? description, string fallbackCategory)
    {
        var text = $"{title} {description}".Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new ListingCategoryResult(fallbackCategory, null, "sale");

        var modality = InferModality(text);

        if (ContainsAny(text, "iphone", "samsung", "galaxy", "smartphone", "téléphone", "telephone", "tablet", "ipad", "infinix", "tecno"))
            return new ListingCategoryResult("electronique", "telephone", modality);

        if (ContainsAny(text, "macbook", "ordinateur", "laptop", "pc ", " pc", "imprimante", "printer", "starlink", "playstation", "xbox", "tv ", " télé", "television"))
            return new ListingCategoryResult("electronique", "informatique", modality);

        if (ContainsAny(text, "toyota", "honda", "mercedes", "voiture", "automobile", " jeep", "jeep ", "4x4", "pick-up", "pickup", "camion", "bus ", "moto", "yamaha", " scooter"))
            return new ListingCategoryResult("vehicules_transport", InferVehicleSubcategory(text), modality);

        if (ContainsAny(text, "appartement", "maison", "villa", "studio", "parcelle", "terrain", "immobilier", "bureau", "entrepôt", "entrepot", "immeuble"))
            return new ListingCategoryResult("immobilier", InferPropertySubcategory(text, modality), modality);

        if (ContainsAny(text, "emploi", "job", "recrutement", "cherche un emploi", "stage", "cv ", " cv"))
            return new ListingCategoryResult("emploi_services", "offre_emploi", "demande");

        if (ContainsAny(text, "meuble", "canapé", "canape", "réfrigérateur", "refrigerateur", "décoration", "decoration", "électroménager", "electromenager"))
            return new ListingCategoryResult("maison_jardin", "meubles", modality);

        if (ContainsAny(text, "service", "nettoyage", "déménagement", "demenagement", "plomberie", "climatisation", "rénovation", "renovation"))
            return new ListingCategoryResult("maison_jardin", "services", modality);

        if (ContainsAny(text, "vêtement", "vetement", "habit", "chaussure", "montre", "bijou", "sac "))
            return new ListingCategoryResult("maison_jardin", "mode", modality);

        if (ContainsAny(text, "groupe électrogène", "groupe electrogene", "générateur", "generateur", "panneau solaire"))
            return new ListingCategoryResult("electronique", "energie", modality);

        return new ListingCategoryResult(fallbackCategory, Slugify(title), modality);
    }

    private static ListingCategoryResult MapRubrique(string rubrique, string? title, string? description)
    {
        var key = rubrique.ToLowerInvariant().Replace('-', '_');
        var modality = InferModality($"{title} {description}");

        if (key.Contains("telephone", StringComparison.Ordinal) || key.Contains("tablette", StringComparison.Ordinal))
            return new ListingCategoryResult("electronique", "telephone", modality);

        if (key.Contains("ordinateur", StringComparison.Ordinal) || key.Contains("informatique", StringComparison.Ordinal)
            || key.Contains("electronique", StringComparison.Ordinal) || key.Contains("hi_fi", StringComparison.Ordinal))
            return new ListingCategoryResult("electronique", "informatique", modality);

        if (key.Contains("automobile", StringComparison.Ordinal) || key.Contains("moto", StringComparison.Ordinal)
            || key.Contains("vehicule", StringComparison.Ordinal) || key.Contains("velo", StringComparison.Ordinal))
            return new ListingCategoryResult("vehicules_transport", InferVehicleSubcategory(title ?? rubrique), modality);

        if (key.Contains("immobilier", StringComparison.Ordinal))
            return new ListingCategoryResult("immobilier", InferPropertySubcategory($"{title} {rubrique}", modality), modality);

        if (key.Contains("emploi", StringComparison.Ordinal))
            return new ListingCategoryResult("emploi_services", "offre_emploi", "demande");

        if (key.Contains("service", StringComparison.Ordinal) || key.Contains("proposition", StringComparison.Ordinal))
            return new ListingCategoryResult("maison_jardin", "services", modality);

        if (key.Contains("maison", StringComparison.Ordinal) || key.Contains("meuble", StringComparison.Ordinal)
            || key.Contains("decoration", StringComparison.Ordinal) || key.Contains("electromenager", StringComparison.Ordinal))
            return new ListingCategoryResult("maison_jardin", "meubles", modality);

        if (key.Contains("mode", StringComparison.Ordinal) || key.Contains("habillement", StringComparison.Ordinal))
            return new ListingCategoryResult("maison_jardin", "mode", modality);

        if (key.Contains("groupe", StringComparison.Ordinal) || key.Contains("electrogene", StringComparison.Ordinal)
            || key.Contains("solaire", StringComparison.Ordinal) || key.Contains("construction", StringComparison.Ordinal))
            return new ListingCategoryResult("electronique", "energie", modality);

        return InferFromText(title, description, "other");
    }

    private static string? ExtractMediaCongoRubrique(string url)
    {
        var match = MediaCongoListingRegex().Match(url);
        if (!match.Success)
            return null;

        var tail = match.Groups[1].Value;
        var parts = tail.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return null;

        var knownPrefixes = new[]
        {
            "pour_la_maison_meubles_decoration_electromenager",
            "ordinateurs_informatique_tv_hi_fi_electronique",
            "telephones_tablettes_et_accessoires",
            "services_propositions_d_affaires",
            "automobile_motos_velos_engins_et_pieces",
            "immobilier_vente_location",
            "recherche_d_emploi",
        };

        var joined = string.Join('_', parts);
        foreach (var prefix in knownPrefixes.OrderByDescending(p => p.Length))
        {
            if (joined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix;
        }

        return parts.Length >= 2 ? string.Join('_', parts.Take(3)) : parts[0];
    }

    private static string InferModality(string text)
    {
        if (ContainsAny(text, "louer", "location", "à louer", "a louer", "rent"))
            return "rent";
        if (ContainsAny(text, "vendre", "vente", "à vendre", "a vendre", "sale"))
            return "sale";
        if (ContainsAny(text, "cherche", "recherche", "besoin"))
            return "demande";
        return "sale";
    }

    private static string InferPropertySubcategory(string text, string modality)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("parcelle") || lower.Contains("terrain"))
            return "parcelle";
        if (lower.Contains("studio"))
            return modality == "rent" ? "studio_a_louer" : "studio_a_vendre";
        if (lower.Contains("maison") || lower.Contains("villa"))
            return modality == "rent" ? "maison_a_louer" : "maison_a_vendre";
        if (lower.Contains("appartement") || lower.Contains("flat"))
            return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
        if (lower.Contains("bureau") || lower.Contains("commercial"))
            return "bureau";
        return modality == "rent" ? "appartement_a_louer" : "appartement_a_vendre";
    }

    private static string InferVehicleSubcategory(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("moto") || lower.Contains("scooter") || lower.Contains("yamaha"))
            return "moto";
        if (lower.Contains("camion") || lower.Contains("bus"))
            return "camion";
        return "voiture";
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var slug = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        return slug.Length > 48 ? slug[..48].Trim('_') : slug;
    }

    [GeneratedRegex(@"annonce-mediacongo-\d+_(.+)\.html", RegexOptions.IgnoreCase)]
    private static partial Regex MediaCongoListingRegex();
}
