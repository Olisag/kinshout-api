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

    [Theory]
    [InlineData("https://www.facebook.com/mmc/posts/123", null, "fb:123")]
    [InlineData("https://www.facebook.com/mmc/posts/123", "https://cdn.example.com/clip.mp4", "fb:123")]
    [InlineData("https://www.facebook.com/mmc/posts/123", "https://www.facebook.com/watch?v=456", "fb:123")]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", "https://www.tiktok.com/@artist/video/1234567890", "yt:abcdefghijk")]
    [InlineData("https://example.com/shared", "https://www.youtube.com/watch?v=abcdefghijk", "yt:abcdefghijk")]
    public void Clip_precedence_preserves_source_fallbacks(string externalUrl, string? attachedUrl, string expected)
    {
        Assert.Equal(expected, MediaCanonicalKey.From(externalUrl, attachedUrl is null ? null : [attachedUrl]));
    }
}
