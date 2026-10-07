using System.Security.Cryptography;
using System.Text;
using Kinshout.Api.Configuration;
using Kinshout.Api.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Services;

public static class EmailConfirmationTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static string Create() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public interface IEmailConfirmationSender
{
    Task SendAsync(User user, string token, CancellationToken ct = default);
}

public class EmailConfirmationSender(IEmailService email, IOptions<EmailSettings> emailOptions) : IEmailConfirmationSender
{
    public Task SendAsync(User user, string token, CancellationToken ct = default)
    {
        var baseUrl = emailOptions.Value.WebBaseUrl.TrimEnd('/');
        var link = $"{baseUrl}/confirm-email?token={Uri.EscapeDataString(token)}";
        var hours = (int)EmailConfirmationTokens.Lifetime.TotalHours;

        var body = $"""
            Bonjour {user.DisplayName},

            Bienvenue sur Kinoiserie ! Pour activer votre compte, confirmez votre adresse e-mail en ouvrant ce lien :

            {link}

            Ce lien est valable {hours} heures. Si vous n'avez pas créé de compte Kinoiserie, ignorez simplement cet e-mail.

            — L'équipe Kinoiserie
            """;

        return email.SendAsync(user.Email, "Confirmez votre adresse e-mail Kinoiserie", body, ct);
    }
}
