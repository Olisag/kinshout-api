using Kinshout.Api.Auth;

namespace Kinshout.Api.Tests;

public class ExternalLoginTokenHelperTests
{
    [Fact]
    public void EnsureGoogleIdTokenFormat_Hs256Token_ThrowsHelpfulMessage()
    {
        // header: {"alg":"HS256","typ":"JWT"}
        const string hs256Token =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ExternalLoginTokenHelper.EnsureGoogleIdTokenFormat(hs256Token));

        Assert.Contains("Kinshout JWT", ex.Message);
    }

    [Fact]
    public void NormalizeIdToken_StripsBearerPrefix()
    {
        const string token = "abc.def.ghi";
        Assert.Equal(token, ExternalLoginTokenHelper.NormalizeIdToken($"Bearer {token}"));
    }
}
