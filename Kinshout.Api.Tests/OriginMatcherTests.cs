using Kinshout.Api.Auth;

namespace Kinshout.Api.Tests;

public class OriginMatcherTests
{
    [Theory]
    [InlineData("https://kinshout.vercel.app", true)]
    [InlineData("https://kinshout-git-main-olivier.vercel.app", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://evil.example", false)]
    public void IsAllowed_MatchesConfiguredOrigins(string origin, bool expected)
    {
        var allowed = new[]
        {
            "https://kinshout.vercel.app",
            "https://*.vercel.app",
            "http://localhost:5173",
        };

        Assert.Equal(expected, OriginMatcher.IsAllowed(origin, allowed));
    }

    [Fact]
    public void IsAllowed_Wildcard_AllowsAnyOrigin()
    {
        Assert.True(OriginMatcher.IsAllowed("https://any.example.com", ["*"]));
        Assert.True(OriginMatcher.IsAllowed(null, ["*"]));
    }

    [Fact]
    public void NormalizeOrigin_StripsRefererPath()
    {
        Assert.Equal(
            "https://kinshout.vercel.app",
            OriginMatcher.NormalizeOrigin("https://kinshout.vercel.app/publish"));
    }
}
