using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchSpellingNormalizerTests
{
    [Theory]
    [InlineData("apartement", "appartement")]
    [InlineData("appartament", "appartement")]
    [InlineData("appertement", "appartement")]
    [InlineData("appart", "appartement")]
    [InlineData("vehicule", "voiture")]
    public void CanonicalizeToken_FixesCommonMisspellings(string input, string expected)
    {
        Assert.Equal(expected, SearchSpellingNormalizer.CanonicalizeToken(input));
    }

    [Fact]
    public void CanonicalizeText_FixesMisspelledSentence()
    {
        var parsed = SearchQueryParser.Parse("je cherche un apartement a louer");

        Assert.Equal("appartement a location", parsed.SubjectText);
    }

    [Fact]
    public void ParseHints_MisspelledApartmentQuery_MatchesImmobilier()
    {
        var hints = SearchQueryResolver.ParseHints("je cherche un apartement a louer");

        Assert.Null(hints.SubcategorySlug);
        Assert.Empty(hints.LocationTerms);
    }
}
