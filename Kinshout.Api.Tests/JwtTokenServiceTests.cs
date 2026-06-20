using System.IdentityModel.Tokens.Jwt;
using Kinshout.Api.Auth;
using Kinshout.Api.Configuration;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Tests;

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateService() =>
        new(Options.Create(new JwtSettings
        {
            SecretKey = "kinshout-test-secret-key-32chars!!",
            Issuer = "kinshout-test",
            UserAudience = "kinshout-user",
            ExpirationMinutes = 120,
        }));

    [Fact]
    public void CreateUserToken_ContainsUserClaims()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "user@kinshout.test",
            DisplayName = "Jane Doe",
        };

        var token = CreateService().CreateUserToken(user, "kinshout-web", out var expiresAt);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(AuthConstants.UserTokenType, jwt.Claims.First(c => c.Type == AuthConstants.TokenTypeClaim).Value);
        Assert.Equal("kinshout-web", jwt.Claims.First(c => c.Type == AuthConstants.ClientIdClaim).Value);
        Assert.Equal(user.Id.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(user.Email, jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.True(expiresAt > DateTime.UtcNow);
    }
}
