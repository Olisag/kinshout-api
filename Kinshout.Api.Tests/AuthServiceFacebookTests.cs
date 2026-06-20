using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AuthServiceFacebookTests
{
    [Fact]
    public async Task SignInWithFacebookAsync_CreatesUserAndLogin()
    {
        await using var db = TestDbFactory.Create();
        var facebook = new Mock<IFacebookAuthValidator>();
        facebook
            .Setup(x => x.ValidateAccessTokenAsync("fb-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacebookUserInfo(
                "fb-123",
                "marie@example.com",
                "Marie K.",
                "https://cdn.example/avatar.jpg"));

        var service = new AuthService(
            db,
            new JwtTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "kinshout-test-secret-key-32chars!!",
                Issuer = "kinshout-test",
                UserAudience = "kinshout-user",
                ClientAudience = "kinshout-client",
            })),
            Options.Create(new OAuthSettings()),
            facebook.Object,
            Mock.Of<ILogger<AuthService>>());

        var auth = await service.SignInWithFacebookAsync("fb-token", "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("Marie K.", auth.User.DisplayName);
        Assert.Equal("marie@example.com", auth.User.Email);
        Assert.Single(db.UserLogins.Where(x => x.Provider == AuthProvider.Facebook && x.ProviderKey == "fb-123"));
    }
}
