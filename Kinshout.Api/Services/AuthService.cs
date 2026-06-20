using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Google.Apis.Auth;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Services;

public interface IAuthService
{
    Task<AuthResponseDto> SignInWithGoogleAsync(string idToken, string clientId, CancellationToken ct = default);
    Task<AuthResponseDto> SignInWithAppleAsync(string idToken, string clientId, CancellationToken ct = default);
    Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<UserProfileDto> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request, CancellationToken ct = default);
}

public class AuthService(
    KinshoutDbContext db,
    IJwtTokenService jwt,
    IOptions<OAuthSettings> oauthOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly OAuthSettings _oauth = oauthOptions.Value;

    public async Task<AuthResponseDto> SignInWithGoogleAsync(string idToken, string clientId, CancellationToken ct = default)
    {
        var payload = await GoogleJsonWebSignature.ValidateAsync(
            idToken,
            new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = string.IsNullOrWhiteSpace(_oauth.Google.ClientId) ? null : [_oauth.Google.ClientId],
            });

        var email = payload.Email ?? throw new UnauthorizedAccessException("Google account has no email.");
        var name = payload.Name ?? email.Split('@')[0];
        var providerKey = payload.Subject;

        return await UpsertExternalLoginAsync(
            AuthProvider.Google,
            providerKey,
            email,
            name,
            payload.Picture,
            clientId,
            ct);
    }

    public async Task<AuthResponseDto> SignInWithAppleAsync(string idToken, string clientId, CancellationToken ct = default)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idToken);
        var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value
            ?? throw new UnauthorizedAccessException("Apple token missing email.");
        var providerKey = jwt.Subject;
        var name = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value ?? email.Split('@')[0];

        if (!string.IsNullOrWhiteSpace(_oauth.Apple.ClientId))
        {
            await ValidateAppleTokenAsync(idToken, ct);
        }
        else
        {
            logger.LogWarning("Apple ClientId not configured — skipping full token validation (dev only).");
        }

        return await UpsertExternalLoginAsync(
            AuthProvider.Apple,
            providerKey,
            email,
            name,
            null,
            clientId,
            ct);
    }

    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : ToProfile(user);
    }

    public async Task<UserProfileDto> UpdateProfileAsync(
        Guid userId,
        UpdateProfileRequestDto request,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        user.WhatsAppNumber = WhatsAppHelper.Normalize(request.WhatsAppNumber);
        await db.SaveChangesAsync(ct);
        return ToProfile(user);
    }

    private async Task<AuthResponseDto> UpsertExternalLoginAsync(
        AuthProvider provider,
        string providerKey,
        string email,
        string displayName,
        string? avatarUrl,
        string clientId,
        CancellationToken ct)
    {
        var login = await db.UserLogins
            .Include(l => l.User)
            .FirstOrDefaultAsync(l => l.Provider == provider && l.ProviderKey == providerKey, ct);

        User user;
        if (login is not null)
        {
            user = login.User;
            user.LastLoginAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(avatarUrl))
                user.AvatarUrl = avatarUrl;
        }
        else
        {
            var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (existing is null)
            {
                user = new User
                {
                    Email = email,
                    DisplayName = displayName,
                    AvatarUrl = avatarUrl,
                    LastLoginAt = DateTime.UtcNow,
                };
                db.Users.Add(user);
            }
            else
            {
                user = existing;
                user.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(avatarUrl))
                    user.AvatarUrl = avatarUrl;
            }

            db.UserLogins.Add(new UserLogin
            {
                User = user,
                Provider = provider,
                ProviderKey = providerKey,
            });
        }

        await db.SaveChangesAsync(ct);
        var token = jwt.CreateUserToken(user, clientId, out var expiresAt);
        return new AuthResponseDto(token, expiresAt, ToProfile(user));
    }

    private async Task ValidateAppleTokenAsync(string idToken, CancellationToken ct)
    {
        using var http = new HttpClient();
        var keysJson = await http.GetStringAsync("https://appleid.apple.com/auth/keys", ct);
        using var doc = JsonDocument.Parse(keysJson);
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idToken);

        var kid = jwt.Header.Kid;
        var keyElement = doc.RootElement.GetProperty("keys").EnumerateArray()
            .FirstOrDefault(k => k.GetProperty("kid").GetString() == kid);

        if (keyElement.ValueKind == JsonValueKind.Undefined)
            throw new UnauthorizedAccessException("Apple signing key not found.");

        var n = Base64UrlEncoder.DecodeBytes(keyElement.GetProperty("n").GetString()!);
        var e = Base64UrlEncoder.DecodeBytes(keyElement.GetProperty("e").GetString()!);
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportParameters(new System.Security.Cryptography.RSAParameters { Modulus = n, Exponent = e });
        var key = new RsaSecurityKey(rsa) { KeyId = kid };

        handler.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidIssuer = "https://appleid.apple.com",
            ValidAudience = _oauth.Apple.ClientId,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        }, out _);
    }

    private static UserProfileDto ToProfile(User user) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            user.AvatarUrl,
            user.WhatsAppNumber,
            !string.IsNullOrWhiteSpace(user.WhatsAppNumber),
            $"Membre depuis {user.CreatedAt:MMM yyyy}");
}
