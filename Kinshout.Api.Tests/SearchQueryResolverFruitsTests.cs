using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchQueryResolverFruitsTests
{
    [Fact]
    public void ParseHints_Fruits_DoesNotInferCategoryOrTopic()
    {
        var hints = SearchQueryResolver.ParseHints("Fruits");

        Assert.Null(hints.SubcategorySlug);
        Assert.Empty(hints.LocationTerms);
    }
}
