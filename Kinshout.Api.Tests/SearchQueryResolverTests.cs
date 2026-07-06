using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchQueryResolverTests
{
    [Theory]
    [InlineData("Appartements a louer", "immobilier", null, null)]
    [InlineData("Appartements à louer", "immobilier", null, null)]
    [InlineData("Immobilier", "immobilier", null, null)]
    [InlineData("iPhone Kinshasa", "telephones", null, null)]
    [InlineData("Voiture Toyota", "vehicules", null, null)]
    [InlineData("appartement Gombe", "immobilier", "appartement_a_louer", "Gombe")]
    [InlineData("Football Kinshasa", null, null, null, "sport")]
    [InlineData("Politique", null, null, null, "politique")]
    public void Parse_ResolvesAdvertAndDiscussionQueries(
        string query,
        string? advertSlug,
        string? subcategorySlug,
        string? location,
        string? topicSlug = null)
    {
        var parsed = SearchQueryResolver.Parse(query, SampleAdvertCategories(), SampleTopicCategories());

        if (advertSlug is null)
            Assert.Null(parsed.AdvertCategory);
        else
            Assert.Equal(advertSlug, parsed.AdvertCategory!.Slug);

        Assert.Equal(subcategorySlug, parsed.SubcategorySlug);

        if (location is null)
            Assert.Empty(parsed.LocationTerms);
        else
            Assert.Contains(location, parsed.LocationTerms, StringComparer.OrdinalIgnoreCase);

        if (topicSlug is null)
            Assert.Null(parsed.DiscussionTopic);
        else
            Assert.Equal(topicSlug, parsed.DiscussionTopic!.Slug);
    }

    [Fact]
    public void Parse_ReturnsEmptyForUnrelatedQuery()
    {
        var parsed = SearchQueryResolver.Parse("xyzqwerty unrelated", SampleAdvertCategories(), SampleTopicCategories());
        Assert.Null(parsed.AdvertCategory);
        Assert.Null(parsed.DiscussionTopic);
    }

    private static List<Category> SampleAdvertCategories() =>
    [
        new() { Slug = "immobilier", Label = "Immobilier", Icon = "⌂", IsAiGenerated = true },
        new() { Slug = "vehicules", Label = "Véhicules", Icon = "🚗", IsAiGenerated = true },
        new() { Slug = "telephones", Label = "Téléphones & tablettes", Icon = "📱", IsAiGenerated = true },
    ];

    private static List<Category> SampleTopicCategories() =>
    [
        new() { Slug = "sport", Label = "Sport & foot", Icon = "⚽", IsDiscussionTopic = true, IsAiGenerated = true },
        new() { Slug = "politique", Label = "Politique", Icon = "🏛️", IsDiscussionTopic = true, IsAiGenerated = true },
    ];
}
