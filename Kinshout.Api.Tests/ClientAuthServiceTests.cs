using System.Text.Json;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Tests;

public class ClientAuthServiceTests
{
    private static ClientAuthService CreateService(KinshoutDbContext db, string secret = "dev-secret")
    {
        var hasher = new PasswordHasher<ApiClient>();
        var client = new ApiClient
        {
            ClientId = "kinshout-web",
            Name = "Kinshout Web",
            AllowedOriginsJson = JsonSerializer.Serialize(new[] { "http://localhost:5173" }),
            IsActive = true,
        };
        client.SecretHash = hasher.HashPassword(client, secret);
        db.ApiClients.Add(client);
        db.SaveChanges();

        var jwt = Options.Create(new JwtSettings
        {
            SecretKey = "kinshout-test-secret-key-32chars!!",
            Issuer = "kinshout-test",
            ClientAudience = "kinshout-client",
            ClientExpirationMinutes = 60,
        });

        var clientAuth = Options.Create(new ClientAuthSettings { AllowAnyOrigin = false });

        return new ClientAuthService(db, jwt, clientAuth, hasher);
    }

    [Fact]
    public async Task AuthenticateAsync_ValidRequest_ReturnsClientToken()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        var response = await service.AuthenticateAsync(
            new ClientAuthRequestDto("kinshout-web", "dev-secret"),
            "http://localhost:5173");

        Assert.False(string.IsNullOrWhiteSpace(response.ClientToken));
        Assert.Equal("kinshout-web", response.ClientId);
        Assert.True(response.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidSecret_Throws()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AuthenticateAsync(
                new ClientAuthRequestDto("kinshout-web", "wrong-secret"),
                "http://localhost:5173"));
    }

    [Fact]
    public async Task AuthenticateAsync_DisallowedOrigin_Throws()
    {
        await using var db = TestDbFactory.Create();
        var service = CreateService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AuthenticateAsync(
                new ClientAuthRequestDto("kinshout-web", "dev-secret"),
                "https://evil.example"));
    }
}
