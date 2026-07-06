using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchQueryResolverTests
{
    [Theory]
    [InlineData("appartement Gombe", "appartement_a_louer", "Gombe")]
    [InlineData("iPhone Kinshasa", null, "Kinshasa")]
    [InlineData("Voiture Toyota", "voiture", null)]
    [InlineData("moto", "moto", null)]
    [InlineData("camion", "camion", null)]
    [InlineData("Kinshasa", null, "Kinshasa")]
    [InlineData("Football Kinshasa", null, "Kinshasa")]
    [InlineData("Fruits", null, null)]
    public void ParseHints_ExtractsSubcategoryAndLocation(
        string query,
        string? subcategorySlug,
        string? location)
    {
        var hints = SearchQueryResolver.ParseHints(query);

        Assert.Equal(subcategorySlug, hints.SubcategorySlug);

        if (location is null)
            Assert.Empty(hints.LocationTerms);
        else
            Assert.Contains(location, hints.LocationTerms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseHints_ReturnsEmptyForUnrelatedQuery()
    {
        var hints = SearchQueryResolver.ParseHints("xyzqwerty unrelated");
        Assert.Null(hints.SubcategorySlug);
        Assert.Empty(hints.LocationTerms);
    }
}
