using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchRelevanceTests
{
    [Fact]
    public void IsDiscussionRelevant_RequiresAllCoreTermsForMarketplaceQuery()
    {
        var assassination = new Discussion
        {
            Title = "Assassinat d'une femme de RDC à Brazzaville",
            Body = "Une femme congolaise a été tuée. Les enfants sont sous choc.",
        };
        var kidsThread = new Discussion
        {
            Title = "Où acheter des vetements pour enfants à Kinshasa ?",
            Body = "Je cherche des adresses fiables pour vetement enfant.",
        };

        Assert.False(SearchRelevance.IsDiscussionRelevant("Vente vetement pour enfant", assassination));
        Assert.True(SearchRelevance.IsDiscussionRelevant("Vente vetement pour enfant", kidsThread));
    }

    [Fact]
    public void IsAdvertRelevant_RejectsSpamWhenGenericServiceTermsPresent()
    {
        var spam = new Advert
        {
            Title = "Amil baba in Pakistan, asli amil baba, black magic expert",
            Description = "Real amil baba service in Karachi, Lahore, Canada, USA, UK",
        };
        var relevant = new Advert
        {
            Title = "Accompagnement numérique pour PME",
            Description = "Coaching et accompagnement numérique",
        };

        // Query without offer template keeps generic "service" in core terms for strict filtering.
        const string genericServiceQuery = "service accompagnement numerique";

        Assert.False(SearchRelevance.IsAdvertRelevant(genericServiceQuery, spam));
        Assert.True(SearchRelevance.IsAdvertRelevant(genericServiceQuery, relevant));
    }

    [Fact]
    public void IsDiscussionRelevant_AllowsSingleTermLocationQuery()
    {
        var kinshasaThread = new Discussion
        {
            Title = "Traffic update",
            Body = "Heavy traffic on boulevard du 30 juin in Kinshasa today.",
        };

        Assert.True(SearchRelevance.IsDiscussionRelevant("Kinshasa", kinshasaThread));
    }

    [Theory]
    [InlineData("moderne societe kinshasa", "mode", false)]
    [InlineData("mode enfants kinshasa", "mode", true)]
    public void MatchesWholeTerm_AvoidsSubstringFalsePositives(string field, string term, bool expected)
    {
        Assert.Equal(expected, SearchRelevance.MatchesWholeTerm(field, term));
    }
}
