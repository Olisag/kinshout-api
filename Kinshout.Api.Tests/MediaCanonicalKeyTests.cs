using Kinshout.Api.Services;

namespace Kinshout.Api.Tests;

public class MediaCanonicalKeyTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", "yt:abcdefghijk")]
    [InlineData("https://youtu.be/abcdefghijk", "yt:abcdefghijk")]
    [InlineData("https://www.youtube.com/shorts/abcdefghijk", "yt:abcdefghijk")]
    [InlineData("https://www.tiktok.com/@deguempro1/video/1234567890", "tt:1234567890")]
    [InlineData("https://x.com/someone/status/1234567890", "x:1234567890")]
    public void Same_clip_keeps_one_key(string url, string expected)
    {
        Assert.Equal(expected, MediaCanonicalKey.FromUrl(url));
    }

    [Fact]
    public void Facebook_share_of_a_youtube_clip_uses_the_youtube_key()
    {
        var key = MediaCanonicalKey.From(
            "https://www.facebook.com/mmc/posts/123",
            ["https://www.youtube.com/watch?v=abcdefghijk"]);

        Assert.Equal("yt:abcdefghijk", key);
    }
}
