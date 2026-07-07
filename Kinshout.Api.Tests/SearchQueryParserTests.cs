using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchQueryParserTests
{
    [Theory]
    [InlineData("je cherche une moto", "moto", SearchIntentHelper.Demande)]
    [InlineData("Je cherche un appartement à Gombe", "appartement a gombe", SearchIntentHelper.Demande)]
    [InlineData("recherche maison Limete", "maison limete", SearchIntentHelper.Demande)]
    [InlineData("looking for apartment in Gombe", "apartment in gombe", SearchIntentHelper.Demande)]
    [InlineData("I need a car", "car", SearchIntentHelper.Demande)]
    [InlineData("nalingi ndako na Gombe", "ndako na gombe", SearchIntentHelper.Demande)]
    [InlineData("nazali koluka motuka", "motuka", SearchIntentHelper.Demande)]
    [InlineData("je vends ma moto", "moto", SearchIntentHelper.Offre)]
    [InlineData("koteka motuka", "motuka", SearchIntentHelper.Offre)]
    [InlineData("selling my Toyota", "toyota", SearchIntentHelper.Offre)]
    public void Parse_ExtractsSubjectAndIntent(string query, string expectedSubject, string expectedIntent)
    {
        var parsed = SearchQueryParser.Parse(query);

        Assert.True(parsed.MatchedPattern);
        Assert.Equal(expectedSubject, parsed.SubjectText);
        Assert.Equal(expectedIntent, parsed.IntentHint);
    }

    [Theory]
    [InlineData("moto")]
    [InlineData("appartement Gombe")]
    [InlineData("iPhone 13")]
    public void Parse_PassthroughForBareKeywords(string query)
    {
        var parsed = SearchQueryParser.Parse(query);

        Assert.False(parsed.MatchedPattern);
        Assert.Equal(SearchTextNormalizer.Normalize(query), parsed.SubjectText);
        Assert.Null(parsed.IntentHint);
    }

    [Fact]
    public void ParseHints_DemandPhrase_AppliesMotoSubcategory()
    {
        var hints = SearchQueryResolver.ParseHints("je cherche une moto");

        Assert.Equal("moto", hints.SubcategorySlug);
    }

    [Fact]
    public void Expand_UsesParsedSubject_NotDemandWrapper()
    {
        var expanded = SearchTermExpander.ExtractExpandedTerms("je cherche une moto");

        Assert.Contains("moto", expanded);
        Assert.DoesNotContain(expanded, term => term is "cherche" or "recherche" or "search");
    }
}
