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
    public void IsBlocked_KeepsKinshasaCityLife(string title, string body)
    {
        Assert.False(PoliticsFeedFilter.IsBlocked(null, title, body, null, null, null));
    }

    [Fact]
    public void RuleVersion_IsAtLeastThree_ForKabilaCirclePass()
    {
        Assert.True(PoliticsFeedFilter.RuleVersion >= 3);
    }
}
