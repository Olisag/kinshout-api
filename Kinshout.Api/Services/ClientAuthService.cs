using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Kinshout.Api.Auth;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Services;

public interface IClientAuthService
{
    Task<ClientAuthResponseDto> AuthenticateAsync(ClientAuthRequestDto request, string? origin, CancellationToken ct = default);
}

public class ClientAuthService(
    KinshoutDbContext db,
    IOptions<JwtSettings> jwtOptions,
    IPasswordHasher<ApiClient> passwordHasher) : IClientAuthService
{
    private readonly JwtSettings _jwt = jwtOptions.Value;

    public async Task<ClientAuthResponseDto> AuthenticateAsync(
        ClientAuthRequestDto request,
        string? origin,
        CancellationToken ct = default)
    {
        var client = await db.ApiClients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == request.ClientId && c.IsActive, ct)
            ?? throw new UnauthorizedAccessException("Unknown or inactive frontend client.");

        var allowedOrigins = JsonSerializer.Deserialize<List<string>>(client.AllowedOriginsJson) ?? [];
        if (allowedOrigins.Count == 0)
            throw new UnauthorizedAccessException("Frontend client has no allowed origins.");

        if (!OriginMatcher.IsAllowed(origin, allowedOrigins))
            throw new UnauthorizedAccessException("Origin not allowed for this frontend client.");

        if (string.IsNullOrWhiteSpace(client.SecretHash))
            throw new UnauthorizedAccessException("Frontend client is not configured with a secret.");

        if (string.IsNullOrWhiteSpace(request.ClientSecret))
            throw new UnauthorizedAccessException("Client secret is required.");

        var verify = passwordHasher.VerifyHashedPassword(client, client.SecretHash, request.ClientSecret);
        if (verify == PasswordVerificationResult.Failed)
            throw new UnauthorizedAccessException("Invalid client secret.");

        var token = CreateClientToken(client, out var expiresAt);
        return new ClientAuthResponseDto(token, expiresAt, client.ClientId, client.Name);
    }

    private string CreateClientToken(ApiClient client, out DateTime expiresAt)
    {
        expiresAt = DateTime.UtcNow.AddMinutes(_jwt.ClientExpirationMinutes);
        var claims = new List<Claim>
        {
            new(AuthConstants.TokenTypeClaim, AuthConstants.AppTokenType),
            new(AuthConstants.ClientIdClaim, client.ClientId),
            new(JwtRegisteredClaimNames.Sub, client.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.ClientAudience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

}
