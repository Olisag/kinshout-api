using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Kinshout.Api.Tests;

public class WhatsAppAuthServiceTests
{
    private static WhatsAppAuthService CreateService(KinshoutDbContext db, IMemoryCache cache)
    {
        var auth = new AuthService(
            db,
            new JwtTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "kinshout-test-secret-key-32chars!!",
                Issuer = "kinshout-test",
                UserAudience = "kinshout-user",
                ClientAudience = "kinshout-client",
            })),
            Options.Create(new OAuthSettings()),
            Mock.Of<ILogger<AuthService>>());

        var env = Mock.Of<IHostEnvironment>();

        return new WhatsAppAuthService(
            cache,
            Options.Create(new WhatsAppAuthSettings
            {
                CodeLength = 6,
                CodeExpirationMinutes = 10,
                ExposeCodeInDevelopment = false,
                ExposeCodeUntilDeliveryEnabled = true,
            }),
            env,
            auth,
            Mock.Of<ILogger<WhatsAppAuthService>>());
    }

    [Fact]
    public async Task VerifyAndSignInAsync_ValidCode_ReturnsUserToken()
    {
        await using var db = TestDbFactory.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(db, cache);

        var sent = await service.SendCodeAsync("900 000 333");
        Assert.NotNull(sent.DebugCode);

        var auth = await service.VerifyAndSignInAsync(
            new WhatsAppVerifyRequestDto("900 000 333", sent.DebugCode!, "Marie K."),
            "kinshout-web");

        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("+243900000333", auth.User.WhatsAppNumber);
        Assert.Equal("Marie K.", auth.User.DisplayName);
        Assert.True(auth.User.HasWhatsApp);
    }

    [Fact]
    public async Task VerifyAndSignInAsync_InvalidCode_Throws()
    {
        await using var db = TestDbFactory.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(db, cache);

        await service.SendCodeAsync("900 000 444");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.VerifyAndSignInAsync(
                new WhatsAppVerifyRequestDto("900 000 444", "000000"),
                "kinshout-web"));
    }
}
