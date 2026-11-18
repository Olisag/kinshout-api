using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class PoliticsFeedFilterTests
{
    [Theory]
    [InlineData("FRN menace Kinshasa", "Le FRN et le Front pour la Refondation de la Nation avancent.")]
    [InlineData("Combats M23", "Les rebelles ont attaqué une position FARDC.")]
    [InlineData("Guerre à l'est", "La guerre civile continue.")]
    [InlineData("Élections", "Le président Tshisekedi parle politique.")]
    [InlineData("Kabila à Kinshasa", "Joseph Kabila et Olive Lembe en déplacement.")]
    [InlineData("Maman wa Roho", "Discours de Maman wa Roho sur la politique.")]
    [InlineData("Un génocide se déroule", "Un génocide se déroule en silence dans la zone de minembwe.")]
    [InlineData("Bombardements à Minembwe", "Le complexe scolaire a subi des bombardements.")]
    [InlineData("Sommet OIF", "La Francophonie et l'OIF réunissent les chefs d'État.")]
    [InlineData("Mobutu quitta Kinshasa", "Il y a 30 ans, Mobutu a quitté Kinshasa.")]
    [InlineData("Clarification Kayikwamba", "La RDC ne quittera pas l'OIF / Francophonie.")]
    public void IsBlocked_HidesPoliticsAndConflict(string title, string body)
    {
        Assert.True(PoliticsFeedFilter.IsBlocked(null, title, body, null, null, null));
    }

    [Theory]
    [InlineData("Concert à Brazzaville", "Soirée rumba à Brazzaville ce samedi.")]
    [InlineData("Pointe-Noire nightlife", "Sortie à Pointe-Noire, République du Congo.")]
    [InlineData("Congo-Brazzaville news", "Actualité Congo Brazzaville.")]
    public void IsBlocked_HidesCongoBrazzaville(string title, string body)
    {
        Assert.True(PoliticsFeedFilter.IsBlocked(null, title, body, null, null, null));
    }

    [Theory]
    [InlineData("Traffic à Gombe", "Embouteillage sur le boulevard du 30 juin à Kinshasa.")]
    [InlineData("Concert Fally", "Fally Ipupa en live à Kinshasa, RDC.")]
    [InlineData("Mode Sape", "La sape et la sapologie à Kinshasa.")]
    [InlineData("Journée de la Francophonie", "Atelier de langue et lecture à Kinshasa.")]
    [InlineData("Club OIF à l'école", "Activité culturelle francophone pour les élèves.")]
    [InlineData("Couple à Kinshasa", "Tu vas la quitter de gré ou de force, blague entre amis.")]
    public void IsBlocked_KeepsKinshasaCityLife(string title, string body)
    {
        Assert.False(PoliticsFeedFilter.IsBlocked(null, title, body, null, null, null));
    }

    [Fact]
    public void Mark_HidesHardBlockedExternalPoliticalScreenshot()
    {
        var discussion = new Kinshout.Api.Models.Discussion
        {
            Title = "Vous allez la quitter de gré ou de force",
            Body = "Blague entre amis.",
            SourceExternalId = "2122617735444119718",
        };

        PoliticsFeedFilter.Mark(discussion);

        Assert.True(discussion.PublicFeedBlocked);
        Assert.Equal(PoliticsFeedFilter.RuleVersion, discussion.PublicFeedRuleVersion);
    }

    [Fact]
    public void RuleVersion_IsAtLeastEight_ForHardBlockedScreenshotPass()
    {
        Assert.True(PoliticsFeedFilter.RuleVersion >= 8);
    }
}
