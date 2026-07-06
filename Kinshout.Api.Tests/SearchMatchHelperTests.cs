using Kinshout.Api.Models;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SearchMatchHelperTests
{
    [Fact]
    public void RankAdvertIds_TitleMatchRanksAboveDescriptionOnly()
    {
        var category = new Category { Label = "Immobilier" };
        var titleMatch = new Advert
        {
            Id = Guid.NewGuid(),
            Title = "Appartement lumineux Gombe",
            Description = "Rien de spécial",
            Category = category,
        };
        var descriptionMatch = new Advert
        {
            Id = Guid.NewGuid(),
            Title = "Belle offre",
            Description = "Grand appartement meublé à Gombe",
            Category = category,
        };

        var ranked = SearchMatchHelper.RankAdvertIds("appartement", [descriptionMatch, titleMatch]);

        Assert.Equal(titleMatch.Id, ranked[0]);
        Assert.Equal(descriptionMatch.Id, ranked[1]);
    }

    [Fact]
    public void RankAdvertIds_PhraseInTitleGetsBonus()
    {
        var category = new Category { Label = "Immobilier" };
        var phraseTitle = new Advert
        {
            Id = Guid.NewGuid(),
            Title = "Studio meublé Gombe",
            Description = "studio disponible",
            Category = category,
        };
        var scatteredTerms = new Advert
        {
            Id = Guid.NewGuid(),
            Title = "Studio",
            Description = "meublé quartier Gombe",
            Category = category,
        };

        var ranked = SearchMatchHelper.RankAdvertIds("studio meuble gombe", [scatteredTerms, phraseTitle]);

        Assert.Equal(phraseTitle.Id, ranked[0]);
    }

    [Fact]
    public void RankDiscussionIds_TitleMatchRanksAboveBodyOnly()
    {
        var category = new Category { Label = "Société" };
        var titleMatch = new Discussion
        {
            Id = Guid.NewGuid(),
            Title = "Quartier calme à Kinshasa",
            Body = "Discutez ici",
            Category = category,
        };
        var bodyMatch = new Discussion
        {
            Id = Guid.NewGuid(),
            Title = "Sujet du jour",
            Body = "Le quartier est très animé à Kinshasa",
            Category = category,
        };

        var ranked = SearchMatchHelper.RankDiscussionIds("quartier", [bodyMatch, titleMatch]);

        Assert.Equal(titleMatch.Id, ranked[0]);
        Assert.Equal(bodyMatch.Id, ranked[1]);
    }
}
