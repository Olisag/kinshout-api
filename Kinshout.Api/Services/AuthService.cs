using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Google.Apis.Auth;
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

public interface IAuthService
{
    Task<AuthResponseDto> SignInWithGoogleAsync(string idToken, string clientId, CancellationToken ct = default);
    Task<AuthResponseDto> SignInWithAppleAsync(string idToken, string clientId, CancellationToken ct = default);
    Task<AuthResponseDto> SignInWithFacebookAsync(string accessToken, string clientId, CancellationToken ct = default);
    Task<EmailConfirmationPendingDto> RegisterWithEmailAsync(EmailRegisterRequestDto request, CancellationToken ct = default);
    Task<AuthResponseDto> LoginWithEmailAsync(EmailLoginRequestDto request, string clientId, CancellationToken ct = default);
    Task<AuthResponseDto> ConfirmEmailAsync(string? token, string clientId, CancellationToken ct = default);
    /// <summary>Sends a fresh link to a pending sign-up. Says nothing about whether the address has an account.</summary>
    Task ResendConfirmationAsync(string? email, CancellationToken ct = default);
    /// <summary>E-mails a password reset link. Says nothing about whether the address has an account.</summary>
    Task RequestPasswordResetAsync(string? email, CancellationToken ct = default);
    /// <summary>Sets a new password from a reset link, signs out every other device and signs the user in.</summary>
    Task<AuthResponseDto> ResetPasswordAsync(
        string? token,
        string? password,
        string clientId,
        CancellationToken ct = default);
    Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<UserProfileDto> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request, CancellationToken ct = default);
    Task<UserProfileDto> UpdateDisplayNameAsync(
        Guid userId,
        UpdateDisplayNameRequestDto request,
        CancellationToken ct = default);
    Task<UserProfileDto> SetAvatarUrlAsync(Guid userId, string avatarUrl, CancellationToken ct = default);
    Task<UserProfileDto> ClearAvatarAsync(Guid userId, CancellationToken ct = default);
    Task<DisplayPreferenceDto> GetDisplayPreferenceAsync(Guid userId, CancellationToken ct = default);
    Task<DisplayPreferenceDto> UpdateDisplayPreferenceAsync(
        Guid userId,
        UpdateDisplayPreferenceRequestDto request,
        CancellationToken ct = default);
    Task<ProfileVisibilityDto> GetProfileVisibilityAsync(Guid userId, CancellationToken ct = default);
    Task<ProfileVisibilityDto> UpdateProfileVisibilityAsync(
        Guid userId,
        UpdateProfileVisibilityRequestDto request,
        CancellationToken ct = default);
}

public class AuthService(
    KinshoutDbContext db,
    IJwtTokenService jwt,
    IUploadStorage uploadStorage,
    IUploadUrlResolver uploadUrls,
    IOptions<OAuthSettings> oauthOptions,
    IFacebookAuthValidator facebookAuth,
    IPasswordHasher<User> passwordHasher,
    IAuthEmailSender authEmails,
    ILogger<AuthService> logger) : IAuthService
{
    private const int DisplayNameMaxLength = 120;
    private const int MinPasswordLength = 8;
    /// <summary>Facebook accounts without a shared e-mail get an undeliverable address on this domain.</summary>
    private const string FacebookPlaceholderEmailDomain = "@facebook.kinshout";
    private static readonly TimeSpan ConfirmationResendCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PasswordResetCooldown = TimeSpan.FromSeconds(60);
    private readonly OAuthSettings _oauth = oauthOptions.Value;

    public async Task<AuthResponseDto> SignInWithGoogleAsync(string idToken, string clientId, CancellationToken ct = default)
    {
        idToken = ExternalLoginTokenHelper.NormalizeIdToken(idToken);
        ExternalLoginTokenHelper.EnsureGoogleIdTokenFormat(idToken);

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = string.IsNullOrWhiteSpace(_oauth.Google.ClientId) ? null : [_oauth.Google.ClientId],
                });
        }
        catch (InvalidJwtException ex)
        {
            logger.LogWarning(ex, "Google ID token validation failed.");
            var hint = string.IsNullOrWhiteSpace(_oauth.Google.ClientId)
                ? " Configure OAuth:Google:ClientId on the API so the token audience matches."
                : " Ensure the token was issued for the configured Google OAuth client ID.";
            throw new UnauthorizedAccessException($"Invalid Google ID token.{hint}");
        }

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

    public async Task<AuthResponseDto> SignInWithFacebookAsync(
        string accessToken,
        string clientId,
        CancellationToken ct = default)
    {
        var profile = await facebookAuth.ValidateAccessTokenAsync(accessToken, ct);
        return await UpsertExternalLoginAsync(
            AuthProvider.Facebook,
            profile.Id,
            profile.Email ?? $"{profile.Id}{FacebookPlaceholderEmailDomain}",
            profile.Name,
            profile.PictureUrl,
            clientId,
            ct);
    }

    public async Task<EmailConfirmationPendingDto> RegisterWithEmailAsync(
        EmailRegisterRequestDto request,
        CancellationToken ct = default)
    {
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? string.Empty;
        if (password.Length < MinPasswordLength)
        {
            throw new EmailAuthException(
                EmailAuthErrorCodes.PasswordTooShort,
                $"Le mot de passe doit contenir au moins {MinPasswordLength} caractères.",
                StatusCodes.Status400BadRequest);
        }

        // An unconfirmed sign-up proves nothing about who owns the address, so signing up again replaces it.
        var pending = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (pending is not null && pending.EmailConfirmedAt is not null)
        {
            throw new EmailAuthException(
                EmailAuthErrorCodes.EmailInUse,
                "Un compte existe déjà avec cet e-mail.",
                StatusCodes.Status409Conflict);
        }

        string displayName;
        try
        {
            displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? email.Split('@')[0]
                : ValidateDisplayName(request.DisplayName);
        }
        catch (ArgumentException ex)
        {
            throw new EmailAuthException(
                EmailAuthErrorCodes.InvalidDisplayName,
                ex.Message,
                StatusCodes.Status400BadRequest);
        }

        if (await IsDisplayNameTakenAsync(displayName, pending?.Id ?? Guid.Empty, ct))
            displayName = await EnsureUniqueDisplayNameAsync(displayName, ct);

        var user = pending ?? new User { Email = email };
        user.DisplayName = displayName;
        user.PasswordHash = passwordHasher.HashPassword(user, password);

        var sendEmail = pending?.EmailConfirmationSentAt is not { } sentAt
            || DateTime.UtcNow - sentAt >= ConfirmationResendCooldown;
        var confirmationToken = sendEmail ? IssueConfirmationToken(user) : null;

        if (pending is null)
        {
            db.Users.Add(user);
            db.UserLogins.Add(new UserLogin
            {
                User = user,
                Provider = AuthProvider.Local,
                ProviderKey = email,
            });
        }
        await db.SaveChangesAsync(ct);
        if (pending is null)
            await CommunitySeed.JoinGeneralCommunityAsync(db, user.Id, ct);

        if (confirmationToken is not null)
            await SendConfirmationAsync(user, confirmationToken, ct);

        return new EmailConfirmationPendingDto(user.Email);
    }

    public async Task<AuthResponseDto> ConfirmEmailAsync(string? token, string clientId, CancellationToken ct = default)
    {
        var trimmed = token?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw InvalidConfirmationToken();

        var hash = AuthEmailTokens.Hash(trimmed);
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailConfirmationTokenHash == hash, ct)
            ?? throw InvalidConfirmationToken();
        if (user.EmailConfirmationSentAt is not { } sentAt
            || DateTime.UtcNow - sentAt > AuthEmailTokens.ConfirmationLifetime)
        {
            throw InvalidConfirmationToken();
        }

        var now = DateTime.UtcNow;
        user.EmailConfirmedAt = now;
        user.EmailConfirmationTokenHash = null;
        user.EmailConfirmationSentAt = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct);

        var jwtToken = jwt.CreateUserToken(user, clientId, out var expiresAt);
        return new AuthResponseDto(jwtToken, expiresAt, ToProfile(user));
    }

    public async Task ResendConfirmationAsync(string? email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null || user.EmailConfirmedAt is not null)
            return;
        if (user.EmailConfirmationSentAt is { } sentAt && DateTime.UtcNow - sentAt < ConfirmationResendCooldown)
            return;

        var confirmationToken = IssueConfirmationToken(user);
        await db.SaveChangesAsync(ct);
        await SendConfirmationAsync(user, confirmationToken, ct);
    }

    public async Task RequestPasswordResetAsync(string? email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.EndsWith(FacebookPlaceholderEmailDomain, StringComparison.Ordinal))
            return;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
            return;
        if (user.PasswordResetSentAt is { } sentAt && DateTime.UtcNow - sentAt < PasswordResetCooldown)
            return;

        var token = AuthEmailTokens.Create();
        user.PasswordResetTokenHash = AuthEmailTokens.Hash(token);
        user.PasswordResetSentAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            await authEmails.SendPasswordResetAsync(user, token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the password reset link to user {UserId}", user.Id);
            user.PasswordResetSentAt = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw new EmailAuthException(
                EmailAuthErrorCodes.PasswordResetEmailFailed,
                "Impossible d'envoyer l'e-mail de réinitialisation pour le moment. Réessayez dans quelques minutes.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    public async Task<AuthResponseDto> ResetPasswordAsync(
        string? token,
        string? password,
        string clientId,
        CancellationToken ct = default)
    {
        var trimmed = token?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw InvalidResetToken();

        var hash = AuthEmailTokens.Hash(trimmed);
        var user = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == hash, ct)
            ?? throw InvalidResetToken();
        if (user.PasswordResetSentAt is not { } sentAt
            || DateTime.UtcNow - sentAt > AuthEmailTokens.PasswordResetLifetime)
        {
            throw InvalidResetToken();
        }

        password ??= string.Empty;
        if (password.Length < MinPasswordLength)
        {
            throw new EmailAuthException(
                EmailAuthErrorCodes.PasswordTooShort,
                $"Le mot de passe doit contenir au moins {MinPasswordLength} caractères.",
                StatusCodes.Status400BadRequest);
        }

        // Second precision: JWTs carry their issue time in whole seconds.
        var now = DateTime.UtcNow;
        var sessionsValidAfter = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));

        user.PasswordHash = passwordHasher.HashPassword(user, password);
        user.PasswordResetTokenHash = null;
        user.PasswordResetSentAt = null;
        user.SessionsValidAfter = sessionsValidAfter;
        user.LastLoginAt = now;
        // Opening the link proves the address, like the confirmation link would have.
        if (user.EmailConfirmedAt is null)
        {
            user.EmailConfirmedAt = now;
            user.EmailConfirmationTokenHash = null;
            user.EmailConfirmationSentAt = null;
        }

        if (!await db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.Provider == AuthProvider.Local, ct))
        {
            db.UserLogins.Add(new UserLogin
            {
                UserId = user.Id,
                Provider = AuthProvider.Local,
                ProviderKey = user.Email,
            });
        }

        await db.SaveChangesAsync(ct);

        try
        {
            await authEmails.SendPasswordChangedAsync(user, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the password change notice to user {UserId}", user.Id);
        }

        var jwtToken = jwt.CreateUserToken(user, clientId, out var expiresAt);
        return new AuthResponseDto(jwtToken, expiresAt, ToProfile(user));
    }

    private static EmailAuthException InvalidResetToken() =>
        new(
            EmailAuthErrorCodes.InvalidResetToken,
            "Ce lien de réinitialisation est invalide ou a expiré.",
            StatusCodes.Status400BadRequest);

    private static string IssueConfirmationToken(User user)
    {
        var token = AuthEmailTokens.Create();
        user.EmailConfirmationTokenHash = AuthEmailTokens.Hash(token);
        user.EmailConfirmationSentAt = DateTime.UtcNow;
        return token;
    }

    private async Task SendConfirmationAsync(User user, string confirmationToken, CancellationToken ct)
    {
        try
        {
            await authEmails.SendConfirmationAsync(user, confirmationToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the e-mail confirmation link to user {UserId}", user.Id);
            user.EmailConfirmationSentAt = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw new EmailAuthException(
                EmailAuthErrorCodes.ConfirmationEmailFailed,
                "Impossible d'envoyer l'e-mail de confirmation pour le moment. Réessayez dans quelques minutes.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static EmailAuthException InvalidConfirmationToken() =>
        new(
            EmailAuthErrorCodes.InvalidConfirmationToken,
            "Ce lien de confirmation est invalide ou a expiré.",
            StatusCodes.Status400BadRequest);

    public async Task<AuthResponseDto> LoginWithEmailAsync(
        EmailLoginRequestDto request,
        string clientId,
        CancellationToken ct = default)
    {
        var email = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct)
            ?? throw InvalidCredentials();

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            throw new EmailAuthException(
                EmailAuthErrorCodes.SocialAccount,
                "Ce compte utilise une connexion sociale. Connectez-vous avec Google, Apple ou Facebook.",
                StatusCodes.Status401Unauthorized);
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
            throw InvalidCredentials();

        if (user.EmailConfirmedAt is null)
        {
            throw new EmailAuthException(
                EmailAuthErrorCodes.EmailNotConfirmed,
                "Confirmez votre adresse e-mail avec le lien que nous vous avons envoyé avant de vous connecter.",
                StatusCodes.Status403Forbidden);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password!);

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var token = jwt.CreateUserToken(user, clientId, out var expiresAt);
        return new AuthResponseDto(token, expiresAt, ToProfile(user));
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

    public async Task<UserProfileDto> UpdateDisplayNameAsync(
        Guid userId,
        UpdateDisplayNameRequestDto request,
        CancellationToken ct = default)
    {
        var displayName = ValidateDisplayName(request.DisplayName);

        if (await IsDisplayNameTakenAsync(displayName, userId, ct))
            throw new InvalidOperationException("Ce nom affiché est déjà pris.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        user.DisplayName = displayName;
        await db.SaveChangesAsync(ct);
        return ToProfile(user);
    }

    public async Task<UserProfileDto> SetAvatarUrlAsync(
        Guid userId,
        string avatarUrl,
        CancellationToken ct = default)
    {
        if (!IsUserAvatarUpload(userId, avatarUrl))
        {
            throw new ArgumentException(
                "URL d'avatar invalide. Téléversez une image via POST /api/auth/me/avatar.");
        }

        var storagePath = uploadUrls.ToStoragePath(avatarUrl)!;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        await DeleteStoredAvatarIfOwnedAsync(userId, user.AvatarUrl, ct);
        user.AvatarUrl = storagePath;
        await db.SaveChangesAsync(ct);
        return ToProfile(user);
    }

    public async Task<UserProfileDto> ClearAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        await DeleteStoredAvatarIfOwnedAsync(userId, user.AvatarUrl, ct);
        user.AvatarUrl = null;
        await db.SaveChangesAsync(ct);
        return ToProfile(user);
    }

    public async Task<DisplayPreferenceDto> GetDisplayPreferenceAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        return new DisplayPreferenceDto(DisplayPreferenceMode.Normalize(user.DisplayPreference));
    }

    public async Task<DisplayPreferenceDto> UpdateDisplayPreferenceAsync(
        Guid userId,
        UpdateDisplayPreferenceRequestDto request,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        user.DisplayPreference = DisplayPreferenceMode.Normalize(request.Mode);
        await db.SaveChangesAsync(ct);
        return new DisplayPreferenceDto(user.DisplayPreference);
    }

    public async Task<ProfileVisibilityDto> GetProfileVisibilityAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        return new ProfileVisibilityDto(user.IsProfilePublic);
    }

    public async Task<ProfileVisibilityDto> UpdateProfileVisibilityAsync(
        Guid userId,
        UpdateProfileVisibilityRequestDto request,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Utilisateur introuvable.");

        user.IsProfilePublic = request.IsPublic;
        await db.SaveChangesAsync(ct);
        return new ProfileVisibilityDto(user.IsProfilePublic);
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
        var isNewUser = false;
        if (login is not null)
        {
            user = login.User;
            user.LastLoginAt = DateTime.UtcNow;
            user.EmailConfirmedAt ??= DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(avatarUrl) && string.IsNullOrWhiteSpace(user.AvatarUrl))
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
                    EmailConfirmedAt = DateTime.UtcNow,
                };
                db.Users.Add(user);
                isNewUser = true;
            }
            else
            {
                user = existing;
                user.LastLoginAt = DateTime.UtcNow;
                if (user.EmailConfirmedAt is null)
                {
                    // The provider vouches for the address; whoever set the pending password never did.
                    user.EmailConfirmedAt = DateTime.UtcNow;
                    user.EmailConfirmationTokenHash = null;
                    user.EmailConfirmationSentAt = null;
                    user.PasswordHash = null;
                    db.UserLogins.RemoveRange(
                        await db.UserLogins
                            .Where(l => l.UserId == user.Id && l.Provider == AuthProvider.Local)
                            .ToListAsync(ct));
                }
                if (!string.IsNullOrWhiteSpace(avatarUrl) && string.IsNullOrWhiteSpace(user.AvatarUrl))
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
        if (isNewUser)
            await CommunitySeed.JoinGeneralCommunityAsync(db, user.Id, ct);

        var token = jwt.CreateUserToken(user, clientId, out var expiresAt);
        return new AuthResponseDto(token, expiresAt, ToProfile(user));
    }

    private static string ValidateDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Le nom affiché est requis.");

        if (trimmed.Length > DisplayNameMaxLength)
        {
            throw new ArgumentException(
                $"Le nom affiché ne peut pas dépasser {DisplayNameMaxLength} caractères.");
        }

        return trimmed;
    }

    private static EmailAuthException InvalidCredentials() =>
        new(
            EmailAuthErrorCodes.InvalidCredentials,
            "E-mail ou mot de passe incorrect.",
            StatusCodes.Status401Unauthorized);

    private static EmailAuthException InvalidEmail(string message) =>
        new(EmailAuthErrorCodes.InvalidEmail, message, StatusCodes.Status400BadRequest);

    private static string NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('@'))
            throw InvalidEmail("Adresse e-mail invalide.");
        if (value.Length > 320)
            throw InvalidEmail("Adresse e-mail trop longue.");
        return value;
    }

    private async Task<string> EnsureUniqueDisplayNameAsync(string baseName, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            var candidate = i == 0 ? baseName : $"{baseName}{i + 1}";
            if (candidate.Length > DisplayNameMaxLength)
                candidate = candidate[..DisplayNameMaxLength];
            if (!await IsDisplayNameTakenAsync(candidate, Guid.Empty, ct))
                return candidate;
        }

        return $"{baseName[..Math.Min(baseName.Length, 100)]}{Guid.NewGuid():N}"[..DisplayNameMaxLength];
    }

    private async Task<bool> IsDisplayNameTakenAsync(
        string displayName,
        Guid excludeUserId,
        CancellationToken ct)
    {
        var normalized = displayName.ToLowerInvariant();
        return await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id != excludeUserId && u.DisplayName.ToLower() == normalized, ct);
    }

    private bool IsUserAvatarUpload(Guid userId, string? avatarUrl)
    {
        var path = uploadUrls.ToStoragePath(avatarUrl);
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith($"/uploads/avatars/{userId:N}/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeleteStoredAvatarIfOwnedAsync(
        Guid userId,
        string? avatarUrl,
        CancellationToken ct)
    {
        var path = uploadUrls.ToStoragePath(avatarUrl);
        if (!IsUserAvatarUpload(userId, path))
            return;

        try
        {
            await uploadStorage.DeleteIfExistsAsync(path!, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete previous avatar {AvatarUrl} for user {UserId}", avatarUrl, userId);
        }
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

    private UserProfileDto ToProfile(User user) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            uploadUrls.ToPublicUrl(user.AvatarUrl),
            user.WhatsAppNumber,
            !string.IsNullOrWhiteSpace(user.WhatsAppNumber),
            DisplayPreferenceMode.Normalize(user.DisplayPreference),
            user.IsProfilePublic,
            $"Membre depuis {user.CreatedAt:MMM yyyy}");
}
