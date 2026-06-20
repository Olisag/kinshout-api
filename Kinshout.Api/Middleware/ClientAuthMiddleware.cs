using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Kinshout.Api.Auth;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kinshout.Api.Middleware;

public class ClientAuthMiddleware(RequestDelegate next, IOptions<JwtSettings> jwtOptions)
{
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/client",
        "/api/health",
    };

    private readonly JwtSettings _jwt = jwtOptions.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || PublicPaths.Contains(path)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(AuthConstants.ClientTokenHeader, out var header)
            || string.IsNullOrWhiteSpace(header))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing frontend client token." });
            return;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(header.ToString(), new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _jwt.Issuer,
                ValidAudience = _jwt.ClientAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey)),
                ClockSkew = TimeSpan.FromMinutes(2),
            }, out _);

            var tokenType = principal.FindFirst(AuthConstants.TokenTypeClaim)?.Value;
            var clientId = principal.FindFirst(AuthConstants.ClientIdClaim)?.Value;
            if (tokenType != AuthConstants.AppTokenType || string.IsNullOrWhiteSpace(clientId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid frontend client token." });
                return;
            }

            context.Items[AuthConstants.ClientContextKey] = clientId;
            await next(context);
        }
        catch (Exception)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired frontend client token." });
        }
    }
}
