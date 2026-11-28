namespace Kinshout.Api.Services.WhatsApp;

public sealed record TrafficRoute(
    string Id,
    string RoadName,
    string Origin,
    string Destination,
    string Segment,
    string ViaMustContain,
    bool Enabled,
    string? DisabledReason);

/// <summary>
/// Routes that stayed on the named road in the proof of concept stay enabled.
/// Routes that geocoded onto another road stay in the catalogue and stay off.
/// </summary>
public static class TrafficRouteCatalogue
{
    public static IReadOnlyList<TrafficRoute> Routes { get; } =
    [
        new("lumumba-ndjili", "Boulevard Lumumba",
            "Echangeur de Limete, Kinshasa, RDC",
            "Aeroport international de N'djili, Kinshasa, RDC",
            "Échangeur de Limete → Aéroport de Ndjili", "lumumba", true, null),
        new("lumumba-masina", "Boulevard Lumumba",
            "Limete, Boulevard Lumumba, Kinshasa, RDC",
            "Masina, Boulevard Lumumba, Kinshasa, RDC",
            "Limete → Masina", "lumumba", true, null),
        new("novembre-martyrs", "Avenue du 24 Novembre",
            "Batetela, Kinshasa, RDC",
            "Stade des Martyrs, Kinshasa, RDC",
            "Batetela → Stade des Martyrs", "novembre", true, null),
        new("juin-safricas", "Boulevard du 30 Juin",
            "Rond-point Safricas, Kinshasa, RDC",
            "Cercle Elaeis, Kinshasa, RDC",
            "Safricas → Cercle Elaïs", "juin", true, null),
        new("bypass-lemba", "By-Pass",
            "Rond-point Ngaba, Kinshasa, RDC",
            "Lemba, Kinshasa, RDC",
            "Ngaba → Lemba", "bypass", true, null),
        new("victoire-matonge", "Rond-point Victoire",
            "Rond-point Victoire, Kinshasa, RDC",
            "Matonge, Kinshasa, RDC",
            "Vers Matonge", "victoire", true, null),
        new("matadi", "Route de Matadi", "Rond-point Socimat, Kinshasa, RDC", "Universite Pedagogique Nationale, Kinshasa, RDC",
            "Socimat → UPN", "matadi", false, "The proof-of-concept itinerary left via Avenue Nguma."),
        new("huileries", "Avenue des Huileries", "Avenue des Huileries, Limete, Kinshasa, RDC", "Kingabwa, Limete, Kinshasa, RDC",
            "Limete → Kingabwa", "huileries", false, "The proof-of-concept itinerary left via Kabinda."),
        new("poids-lourds", "Route des Poids Lourds", "Route des Poids Lourds, Limete, Kinshasa, RDC", "Masina, Kinshasa, RDC",
            "Limete → Masina", "poids", false, "The proof-of-concept itinerary stayed on Lumumba and is already measured there."),
        new("sendwe", "Boulevard Sendwe", "Hopital Sendwe, Kinshasa, RDC", "Rond-point Victoire, Kinshasa, RDC",
            "Sendwe → Victoire", "sendwe", false, "The place resolved outside Kinshasa."),
        new("universite", "Avenue de l'Université", "Rond-point Ngaba, Kinshasa, RDC", "Universite de Kinshasa, Kinshasa, RDC",
            "Ngaba → UNIKIN", "universit", false, "The proof-of-concept itinerary used Route Kimwenza."),
        new("kasa-vubu", "Avenue Kasa-Vubu", "Rond-point Victoire, Kinshasa, RDC", "Avenue Kasa-Vubu, Kasa-Vubu, Kinshasa, RDC",
            "Victoire → Kasa-Vubu", "kasa", false, "The proof-of-concept itinerary mixed the N1 and the avenue."),
        new("mandela", "Rond-point Mandela", "Avenue Mandela, Kinshasa, RDC", "Rond-point Socimat, Kinshasa, RDC",
            "Mandela → Socimat", "mandela", false, "The place resolved to Brazzaville."),
    ];

    public static bool ViaMatches(TrafficRoute route, string? via)
    {
        if (string.IsNullOrWhiteSpace(via))
            return false;
        var haystack = via.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var needle = route.ViaMustContain.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (needle == "bypass")
            return haystack.Contains("bypass", StringComparison.Ordinal) || haystack.Contains("bretelle", StringComparison.Ordinal);
        return haystack.Contains(needle, StringComparison.Ordinal);
    }
}
