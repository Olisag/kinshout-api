using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class SourceUnavailableTests
{
    [Fact]
    public void Facebook_plugin_html_marks_a_removed_post()
    {
        const string html = "Cette publication Facebook n’est plus disponible. Elle a peut-être été supprimée.";

        Assert.True(SourceUnavailable.FacebookPluginSaysGone(html));
        Assert.True(SourceUnavailable.FacebookPluginSaysGone(
            "This Facebook post is no longer available. It may have been removed."));
    }

    [Fact]
    public void Facebook_bootloader_word_does_not_count_as_removed()
    {
        const string html = """{"enableLoadingUnavailableResources":true}""";

        Assert.False(SourceUnavailable.FacebookPluginSaysGone(html));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", "https://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://youtu.be/abcdefghijk", "https://www.youtube.com/watch?v=abcdefghijk")]
    public void YouTube_watch_url_uses_the_video_id(string url, string expected)
    {
        Assert.True(SourceUnavailable.TryYouTubeWatchUrl(url, out var watchUrl));
        Assert.Equal(expected, watchUrl);
    }
}
