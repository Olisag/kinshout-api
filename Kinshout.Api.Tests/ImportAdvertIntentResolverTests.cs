using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class ImportAdvertIntentResolverTests
{
    [Theory]
    [InlineData("Cherche appartement à Gombe", "rent", "appartement_a_louer", AdvertIntent.Demande)]
    [InlineData("Appartement 3 chambres à louer", "rent", "appartement_a_louer", AdvertIntent.Offre)]
    [InlineData("Recrute comptable", "offre", "offre_emploi", AdvertIntent.Offre)]
    [InlineData("Cherche un emploi de chauffeur", "sale", "offre_emploi", AdvertIntent.Demande)]
    [InlineData("iPhone 14 Pro Max", "sale", "telephone", AdvertIntent.Offre)]
    [InlineData("Looking for apartment in Gombe", "rent", "appartement_a_louer", AdvertIntent.Demande)]
    public void Resolve_UsesTitleAndModalitySignals(
        string title,
        string modality,
        string subcategory,
        AdvertIntent expected)
    {
        var item = BuildItem(title, modality, subcategory);
        Assert.Equal(expected, ImportAdvertIntentResolver.Resolve(item));
    }

    [Fact]
    public void Resolve_HonorsExplicitDemandeIntentToken()
    {
        var item = BuildItem("Studio Limete", "rent", "studio_a_louer") with
        {
            Ai = new ImportExternalAdvertAiDto(null, null, ["demande"]),
        };

        Assert.Equal(AdvertIntent.Demande, ImportAdvertIntentResolver.Resolve(item));
    }

    private static ImportExternalAdvertDto BuildItem(string title, string modality, string subcategory) =>
        new(
            new ImportExternalAdvertSourceDto(
                AdvertSourceProvider.FacebookMarketplace,
                "Facebook",
                "x-1",
                "https://example.com/x-1",
                DateTime.UtcNow,
                DateTime.UtcNow,
                DateTime.UtcNow),
            "immobilier",
            subcategory,
            title,
            null,
            null,
            null,
            title,
            ["https://example.com/photo.jpg"],
            null,
            Modality: modality);
}
