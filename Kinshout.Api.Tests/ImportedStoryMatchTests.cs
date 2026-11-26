using System.Text.Json;
using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class ImportedStoryMatchTests
{
    [Fact]
    public void NearCopy_MatchesARepostedCaption()
    {
        const string caption = "Une maman que certains Congolais croyaient deja decedee a ete retrouvee en train de se balader dans les rues de Kinshasa ce matin";

        Assert.True(ImportedStoryMatch.IsNearCopy(caption, caption, caption, caption));
    }

    [Fact]
    public void NearCopy_IgnoresASharedName()
    {
        Assert.False(ImportedStoryMatch.IsNearCopy(
            "Werrason annonce un concert",
            "Werrason sera sur scene samedi a la Gombe.",
            "Werrason sort un album",
            "Le nouvel album de Werrason sort le mois prochain."));
    }

    [Fact]
    public void NearCopy_KeepsTwoSessionsThatShareAFooter()
    {
        const string footer = "Devenez partenaire de Moise Mbiye Ministries. Contactez le secretariat pour les details.";

        Assert.False(ImportedStoryMatch.IsNearCopy(
            "CULTE DES MIRACLES ET DELIVRANCE MERCREDI 04 NOV 2026 SESSION DU SOIR",
            footer,
            "CULTE DE DELIVRANCE ET MIRACLE MERCREDI 04.11.2026 VACATION MATIN",
            footer));
    }

    [Fact]
    public void NearCopy_LeavesDifferentlyWordedTellingsToTheRewrite()
    {
        Assert.False(ImportedStoryMatch.IsNearCopy(
            "Une maman retrouvee dans les rues de Kinshasa",
            "Une maman que certains Congolais croyaient deja decedee a ete retrouvee en train de se balader dans les rues de Kinshasa.",
            "Video troublante au Congo",
            "Une video troublante au Congo montre une dame revenue a la vie trois ans apres son deces presume."));
    }

    [Fact]
    public void TryReadSameStory_AcceptsOnlyAListedNumber()
    {
        using var listed = JsonDocument.Parse("{\"sameStory\":1}");
        using var none = JsonDocument.Parse("{\"sameStory\":null}");
        using var outside = JsonDocument.Parse("{\"sameStory\":4}");

        Assert.True(ImportedStoryMatch.TryReadSameStory(listed.RootElement, 2));
        Assert.False(ImportedStoryMatch.TryReadSameStory(none.RootElement, 2));
        Assert.False(ImportedStoryMatch.TryReadSameStory(outside.RootElement, 2));
        Assert.False(ImportedStoryMatch.TryReadSameStory(listed.RootElement, 0));
    }
}
