using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchQueryComplexityTests
{
    [Theory]
    [InlineData("Kinshasa")]
    [InlineData("appartement Gombe")]
    [InlineData("je cherche une moto")]
    [InlineData("Felix")]
    public void ShouldUseAiUnderstanding_ReturnsFalseForSimpleQueries(string query)
    {
        var parsed = SearchQueryParser.Parse(query);
        var hints = SearchQueryResolver.ParseHints(parsed);

        Assert.False(SearchQueryComplexity.ShouldUseAiUnderstanding(parsed, hints));
    }

    [Fact]
    public void ShouldUseAiUnderstanding_ReturnsTrueForLongMixedQueryWithoutTemplate()
    {
        var query = "Je suis a la recherche d une petite maison pas chere dans le quartier Gombe";
        var parsed = SearchQueryParser.Parse(query);
        var hints = SearchQueryResolver.ParseHints(parsed);

        Assert.False(parsed.MatchedPattern);
        Assert.True(SearchQueryComplexity.ShouldUseAiUnderstanding(parsed, hints));
    }

    [Fact]
    public void NazoLukaTemplate_IsHandledLocallyWithoutAi()
    {
        var query = "Nazo luka ndako pas tres cher a Gombe";
        var parsed = SearchQueryParser.Parse(query);

        Assert.True(parsed.MatchedPattern);
        Assert.Equal(SearchIntentHelper.Demande, parsed.IntentHint);
        Assert.Contains("ndako", parsed.SubjectText, StringComparison.Ordinal);
    }
}
