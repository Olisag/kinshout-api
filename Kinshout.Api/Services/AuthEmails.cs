using System.Security.Cryptography;
using System.Text;
using Kinshout.Api.Configuration;
using Kinshout.Api.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Services;

/// <summary>Single-use tokens for e-mailed links. Only their hash is stored.</summary>
public static class AuthEmailTokens
{
    public static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    public static string Create() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public interface IAuthEmailSender
{
    Task SendConfirmationAsync(User user, string token, CancellationToken ct = default);
    Task SendPasswordResetAsync(User user, string token, CancellationToken ct = default);
    Task SendPasswordChangedAsync(User user, CancellationToken ct = default);
}

public class AuthEmailSender(IEmailService email, IOptions<EmailSettings> emailOptions) : IAuthEmailSender
{
    private string BaseUrl => emailOptions.Value.WebBaseUrl.TrimEnd('/');

    public Task SendConfirmationAsync(User user, string token, CancellationToken ct = default)
    {
        var link = $"{BaseUrl}/confirm-email?token={Uri.EscapeDataString(token)}";
        var hours = (int)AuthEmailTokens.ConfirmationLifetime.TotalHours;

        var body = $"""
            Bonjour {user.DisplayName},

            Bienvenue sur Kinoiserie ! Pour activer votre compte, confirmez votre adresse e-mail en ouvrant ce lien :

            {link}

            Ce lien est valable {hours} heures. Si vous n'avez pas créé de compte Kinoiserie, ignorez simplement cet e-mail.

            — L'équipe Kinoiserie
            """;

        return email.SendAsync(user.Email, "Confirmez votre adresse e-mail Kinoiserie", body, ct);
    }

    public Task SendPasswordResetAsync(User user, string token, CancellationToken ct = default)
    {
        var link = $"{BaseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
        var minutes = (int)AuthEmailTokens.PasswordResetLifetime.TotalMinutes;

        var body = $"""
            Bonjour {user.DisplayName},

            Nous avons reçu une demande de réinitialisation du mot de passe de votre compte Kinoiserie. Pour choisir un nouveau mot de passe, ouvrez ce lien :

            {link}

            Ce lien est valable {minutes} minutes et ne sert qu'une fois. Si vous n'êtes pas à l'origine de cette demande, ignorez cet e-mail : votre mot de passe ne changera pas.

            — L'équipe Kinoiserie
            """;

        return email.SendAsync(user.Email, "Réinitialisez votre mot de passe Kinoiserie", body, ct);
    }

    public Task SendPasswordChangedAsync(User user, CancellationToken ct = default)
    {
        var body = $"""
            Bonjour {user.DisplayName},

            Le mot de passe de votre compte Kinoiserie vient d'être modifié, et vos autres appareils ont été déconnectés.

            Si c'est bien vous, vous n'avez rien à faire. Sinon, choisissez tout de suite un nouveau mot de passe :

            {BaseUrl}/forgot-password

            — L'équipe Kinoiserie
            """;

        return email.SendAsync(user.Email, "Votre mot de passe Kinoiserie a été modifié", body, ct);
    }
}
