using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchQueryHelperTests
{
    [Theory]
    [InlineData("Appartement à Gombe", "appart gombe")]
    [InlineData("  appartement   à gombe  ", "appartement gombe")]
    [InlineData("Appartement à louer à Gombe", "appartement gombe location")]
    [InlineData("location appart gombe", "appartement gombe location")]
    [InlineData("Je cherche un chauffeur", "chauffeur")]
    [InlineData("Je cherche un apartment à Gombe", "Je cherche un appartement à Gombe")]
    [InlineData("chauffeur", "chauffeur")]
    [InlineData("iPhone 13 pas cher", "13 cher iphone pas")]
    [InlineData("Discussion sur Starlink", "discussion starlink")]
    [InlineData("discussions starlink", "discussion starlink")]
    [InlineData("Maison à vendre Bandal", "bandal maison vente")]
    [InlineData("Générateur solaire", "generateur solaire")]
    public void CanonicalKey_TreatsSimilarQueriesAsSame(string left, string right)
    {
        var leftKey = SearchQueryHelper.CanonicalKey(left);
        var rightKey = SearchQueryHelper.CanonicalKey(right);

        Assert.NotNull(leftKey);
        Assert.Equal(leftKey, rightKey);
    }

    [Fact]
    public void CanonicalKey_IgnoresWordOrder()
    {
        var a = SearchQueryHelper.CanonicalKey("moto yamaha kinshasa");
        var b = SearchQueryHelper.CanonicalKey("kinshasa yamaha moto");

        Assert.Equal(a, b);
    }

    [Fact]
    public void CanonicalKey_ReturnsNullForTooShortInput()
    {
        Assert.Null(SearchQueryHelper.CanonicalKey("a"));
        Assert.Null(SearchQueryHelper.CanonicalKey("   "));
    }
}
