using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class PopularSearchGrouperTests
{
    [Theory]
    [InlineData("Je cherche un appartement à Gombe", "appartement gombe")]
    [InlineData("appartement Gombe", "appartement gombe")]
    [InlineData("Vêtement enfant", "vetement enfant")]
    [InlineData("vente vetement pour enfant", "vetement enfant")]
    [InlineData("Maison Limete", "maison limete")]
    [InlineData("Ndako na Gombe", "ndako gombe")]
    [InlineData("ndako pas tres cher a gombe", "ndako gombe")]
    [InlineData("Moto à vendre", "moto vente")]
    [InlineData("Kinshasa", "kinshasa")]
    [InlineData("Offre de service d'accompagnement numérique", "accompagnement numerique")]
    public void PhraseKey_PreservesOrderedMeaningfulTokens(string query, string expected)
    {
        Assert.Equal(expected, SearchQueryHelper.PhraseKey(query));
    }

    [Fact]
    public void PhraseKey_DoesNotSortTokens()
    {
        var key = SearchQueryHelper.PhraseKey("maison limete");
        Assert.Equal("maison limete", key);
        Assert.NotEqual("limete maison", key);
    }

    [Fact]
    public void MergePhraseGroups_AbsorbsShorterPrefixIntoLonger()
    {
        var groups = new Dictionary<string, PopularSearchGrouper.PhraseAccumulator>(StringComparer.Ordinal)
        {
            ["appartement"] = CreateAccumulator("appartement", 5, "appartement"),
            ["appartement gombe"] = CreateAccumulator("appartement gombe", 41, "Appartement Gombe"),
        };

        var merged = PopularSearchGrouper.MergePhraseGroups(groups);

        Assert.Single(merged);
        Assert.Equal(46, merged["appartement gombe"].Count);
    }

    [Fact]
    public void Aggregate_MergesPrefixVariantsAndBuildsReadableLabel()
    {
        var rows = new[]
        {
            Stat("appartement", "appartement", 5),
            Stat("appartement gombe", "Je cherche un appartement à Gombe", 41),
        };

        var result = PopularSearchGrouper.Aggregate(rows);

        Assert.Single(result);
        Assert.Equal(46, result[0].Count);
        Assert.Equal("Appartement Gombe", result[0].DisplayLabel);
    }

    [Fact]
    public void Aggregate_ProducesProdStyleTopEntriesWithFusion()
    {
        var rows = new[]
        {
            Stat("vetement enfant", "Vêtement enfant", 70),
            Stat("appartement gombe", "Appartement Gombe", 41),
            Stat("maison limete", "Maison Limete", 27),
            Stat("ndako gombe", "Ndako Gombe", 20),
            Stat("accompagnement numerique", "Accompagnement numérique", 16),
            Stat("felix tshisekedi", "Félix Tshisekedi", 15),
            Stat("voiture gombe", "Voiture Gombe", 13),
            Stat("kinshasa", "Kinshasa", 11),
            Stat("lime", "Lime", 7),
            Stat("moto vente", "Moto à vendre", 5),
            Stat("appartement", "appartement", 3),
            Stat("maison", "maison", 2),
            Stat("voiture", "voiture", 1),
        };

        var result = PopularSearchGrouper.Aggregate(rows).Take(10).ToList();

        Assert.Equal(10, result.Count);
        Assert.Equal("Vêtement Enfant", result[0].DisplayLabel);
        Assert.Equal(70, result[0].Count);
        Assert.Equal("Appartement Gombe", result[1].DisplayLabel);
        Assert.Equal(44, result[1].Count);
        Assert.Equal("Maison Limete", result[2].DisplayLabel);
        Assert.Equal(29, result[2].Count);
        Assert.Equal("Ndako Gombe", result[3].DisplayLabel);
        Assert.Equal(20, result[3].Count);
        Assert.Equal("Moto Vendre", result[9].DisplayLabel);
        Assert.Equal(5, result[9].Count);
    }

    [Fact]
    public void Aggregate_FiltersTreArtifactFromNdakoLabel()
    {
        var rows = new[]
        {
            Stat("ndako gombe", "Ndako tre Gombe", 12),
            Stat("ndako gombe", "Ndako Gombe", 8),
        };

        var result = PopularSearchGrouper.Aggregate(rows);

        Assert.Single(result);
        Assert.Equal(20, result[0].Count);
        Assert.Equal("Ndako Gombe", result[0].DisplayLabel);
    }

    [Fact]
    public void Aggregate_MergesFelixPrefixIntoFullName()
    {
        var rows = new[]
        {
            Stat("felix", "Felix", 4),
            Stat("felix tshisekedi", "Félix Tshisekedi", 15),
        };

        var result = PopularSearchGrouper.Aggregate(rows);

        Assert.Single(result);
        Assert.Equal(19, result[0].Count);
        Assert.Equal("Félix Tshisekedi", result[0].DisplayLabel);
    }

    [Fact]
    public void Aggregate_ResolvesLegacyCanonicalKeyFromDisplayQuery()
    {
        // Old rows stored bag-of-words CanonicalKey in NormalizedQuery; read path uses DisplayQuery.
        var rows = new[]
        {
            Stat("appartement gombe", "Je cherche un appartement à Gombe", 5),
            Stat("gombe appartement", "Appartement Gombe", 3),
        };

        var result = PopularSearchGrouper.Aggregate(rows);

        Assert.Single(result);
        Assert.Equal(8, result[0].Count);
        Assert.Equal("Appartement Gombe", result[0].DisplayLabel);
    }

    private static SearchQueryStat Stat(string normalized, string display, int count) =>
        new()
        {
            NormalizedQuery = normalized,
            DisplayQuery = display,
            SearchCount = count,
            LastSearchedAt = DateTime.UtcNow,
        };

    private static PopularSearchGrouper.PhraseAccumulator CreateAccumulator(
        string phraseKey,
        int count,
        string display)
    {
        var accumulator = new PopularSearchGrouper.PhraseAccumulator(phraseKey);
        accumulator.Add(Stat(phraseKey, display, count));
        return accumulator;
    }
}
