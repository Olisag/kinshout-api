using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class ExternalAdvertImportEnrichmentTests
{
    [Fact]
    public void IsMeaningful_RejectsShortOrTitleOnlyDescriptions()
    {
        Assert.False(ImportDescriptionQuality.IsMeaningful("Studio Gombe", "Studio Gombe", null));
        Assert.False(ImportDescriptionQuality.IsMeaningful("Court texte.", "Titre long différent", null));
        Assert.True(ImportDescriptionQuality.IsMeaningful(
            "Bel appartement lumineux de 3 chambres à Gombe avec parking et balcon.",
            "Appartement Gombe",
            null));
    }

    [Fact]
    public void BuildStructuredDescription_CombinesAvailableFields()
    {
        var item = new ImportExternalAdvertDto(
            new ImportExternalAdvertSourceDto("facebook_marketplace", "Facebook", "1", "https://x", DateTime.UtcNow, DateTime.UtcNow, null),
            "immobilier",
            "appartement_a_louer",
            "Appartement Gombe",
            new ImportExternalAdvertPriceDto(1500, "USD", "$1 500 / mois", "monthly", false),
            new ImportExternalAdvertLocationDto("Kinshasa", "Gombe", null, null, "Gombe, Kinshasa"),
            new ImportExternalAdvertDetailsDto(2, 1, 80, true, null, "apartment", null, null, null, null),
            "Gombe",
            ["https://example.com/1.jpg"],
            null);

        var category = new Category { Label = "Immobilier", Slug = "immobilier", Icon = "🏠" };
        var description = ExternalAdvertImportEnrichmentService.BuildStructuredDescription(item, category);

        Assert.Contains("Appartement Gombe", description);
        Assert.Contains("Gombe", description);
        Assert.Contains("$1 500 / mois", description);
        Assert.Contains("2 chambres", description);
    }
}
