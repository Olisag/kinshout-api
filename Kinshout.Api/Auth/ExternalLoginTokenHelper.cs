using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Auth;

public static class ExternalLoginTokenHelper
{
    public static string NormalizeIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            throw new ArgumentException("idToken is required.");

        var token = idToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        return token;
    }

    /// <summary>
    /// Google ID tokens are RS256 JWTs from accounts.google.com — not Kinshout client/user tokens (HS256).
    /// </summary>
    public static void EnsureGoogleIdTokenFormat(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw new UnauthorizedAccessException(
                "Invalid idToken format. Send the Google ID token (credential JWT from Google Sign-In), not an access token.");
        }

        string alg;
        try
        {
            var headerJson = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[0]));
            using var doc = JsonDocument.Parse(headerJson);
            alg = doc.RootElement.TryGetProperty("alg", out var algProp)
                ? algProp.GetString() ?? ""
                : "";
        }
        catch
        {
            throw new UnauthorizedAccessException("Invalid idToken format.");
        }

        if (string.Equals(alg, "RS256", StringComparison.Ordinal))
            return;

        if (string.Equals(alg, "HS256", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "This is a Kinshout JWT (HS256), not a Google ID token. " +
                "Call POST /api/auth/client first for X-Kinshout-Client-Token, then send a Google ID token in the body idToken field. " +
                "Do not paste the client token or Kinshout user token into idToken.");
        }

        throw new UnauthorizedAccessException(
            $"Expected a Google ID token (RS256). The supplied token uses '{(string.IsNullOrEmpty(alg) ? "unknown" : alg)}'.");
    }
}
