using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Kinshout.ExternalImporter.Providers.Scraping;

namespace Kinshout.Api.Tests;

public class BuzzImportTests
{
    [Fact]
    public void Score_WeightsCommentsAndSharesAboveLikes()
    {
        var likesOnly = BuzzGate.Score(likes: 30, comments: 0, shares: 0);
        var passedAround = BuzzGate.Score(likes: 0, comments: 6, shares: 5);

        Assert.True(passedAround > likesOnly);
        Assert.Equal(33, passedAround);
    }

    [Fact]
    public void Insolite_IsNotFiledAsHumour()
    {
        Assert.Null(CongoleseSocialTopic.Slug("Moment insolite à Kinshasa", "Les gens filment la scène"));
        Assert.Equal("humour", CongoleseSocialTopic.Slug("Sketch au marché", "Une blague de quartier"));
    }

    [Fact]
    public void BuzzingText_StaysWithoutAPhotoOrVideo()
    {
        var post = new Discussion { SourceEngagementScore = DiscussionMultimedia.BuzzEngagementFloor };

        Assert.True(DiscussionMultimedia.IsBuzzingEngagement(post.SourceEngagementScore));
        Assert.True(DiscussionMultimedia.ShouldKeepOnVisualFeed(post));
        Assert.False(DiscussionMultimedia.ShouldKeepOnVisualFeed(new Discussion { SourceEngagementScore = 5 }));
    }
}
