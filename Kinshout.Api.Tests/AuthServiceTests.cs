using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class AuthServiceTests
{
    [Fact]
    public async Task UpdateProfileAsync_SavesNormalizedWhatsApp()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db, withWhatsApp: false);

        var service = new AuthService(
            db,
            new JwtTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "kinshout-test-secret-key-32chars!!",
                Issuer = "kinshout-test",
                UserAudience = "kinshout-user",
            })),
            Options.Create(new OAuthSettings()),
            Mock.Of<IFacebookAuthValidator>(),
            Mock.Of<ILogger<AuthService>>());

        var profile = await service.UpdateProfileAsync(
            user.Id,
            new UpdateProfileRequestDto("900 000 222"));

        Assert.Equal("+243900000222", profile.WhatsAppNumber);
        Assert.True(profile.HasWhatsApp);
    }

    [Fact]
    public async Task GetProfileAsync_ReflectsWhatsApp()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = await TestDbFactory.SeedUserAndCategoryAsync(db);

        var service = new AuthService(
            db,
            new JwtTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "kinshout-test-secret-key-32chars!!",
                Issuer = "kinshout-test",
                UserAudience = "kinshout-user",
            })),
            Options.Create(new OAuthSettings()),
            Mock.Of<IFacebookAuthValidator>(),
            Mock.Of<ILogger<AuthService>>());

        var profile = await service.GetProfileAsync(user.Id);

        Assert.NotNull(profile);
        Assert.True(profile!.HasWhatsApp);
        Assert.Equal("+243900000001", profile.WhatsAppNumber);
    }
}
