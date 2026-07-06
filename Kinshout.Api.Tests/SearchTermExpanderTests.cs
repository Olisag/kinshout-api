using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchTermExpanderTests
{
    [Fact]
    public void Expand_ApartmentIncludesFrenchEquivalent()
    {
        var expanded = SearchTermExpander.Expand(["apartment"]);

        Assert.Contains("appartement", expanded);
        Assert.Contains("apartment", expanded);
    }

    [Fact]
    public void Expand_MotukaIncludesFrenchEquivalent()
    {
        var expanded = SearchTermExpander.Expand(["motuka"]);

        Assert.Contains("motuka", expanded);
        Assert.Contains("voiture", expanded);
    }

    [Fact]
    public void QueryMatchesConcept_DetectsEnglishHousingTerms()
    {
        Assert.True(SearchTermExpander.QueryMatchesConcept("looking for apartment in gombe", SearchConcept.Immobilier));
        Assert.True(SearchTermExpander.QueryMatchesConcept("ndako na gombe", SearchConcept.Immobilier));
    }

    [Fact]
    public void QueryMatchesConcept_DetectsLingalaVehicleTerms()
    {
        Assert.True(SearchTermExpander.QueryMatchesConcept("nazali koteka motuka", SearchConcept.Vehicules));
    }
}

public class SearchMultilingualMatchTests
{
    [Fact]
    public void RankAdvertIds_EnglishQueryMatchesFrenchListing()
    {
        var category = new Category { Label = "Immobilier" };
        var frenchListing = new Advert
        {
            Id = Guid.NewGuid(),
            Title = "Bel appartement meublé Gombe",
            Description = "Disponible immédiatement",
            Category = category,
        };

        var ranked = SearchMatchHelper.RankAdvertIds("apartment gombe", [frenchListing]);

        Assert.Single(ranked);
        Assert.Equal(frenchListing.Id, ranked[0]);
    }

    [Fact]
    public void RankAdvertIds_LingalaQueryMatchesFrenchListing()
    {
        var category = new Category { Label = "Véhicules" };
        var frenchListing = new Advert
        {
            Id = Guid.NewGuid(),
            Title = "Toyota RAV4 occasion",
            Description = "Voiture en bon état",
            Category = category,
        };

        var ranked = SearchMatchHelper.RankAdvertIds("motuka toyota", [frenchListing]);

        Assert.Single(ranked);
        Assert.Equal(frenchListing.Id, ranked[0]);
    }
}
