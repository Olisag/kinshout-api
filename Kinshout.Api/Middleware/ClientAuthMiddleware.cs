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
    private readonly TokenValidationParameters _clientValidation = BuildValidationParameters(jwtOptions.Value);

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

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var candidates = await CollectClientTokenCandidatesAsync(context);
        if (candidates.Count == 0)
        {
            await Write401Async(context, "Missing frontend client token.");
            return;
        }

        foreach (var token in candidates)
        {
            if (TryValidateClientToken(token, out var clientId))
            {
                context.Items[AuthConstants.ClientContextKey] = clientId;
                await next(context);
                return;
            }
        }

        if (candidates.Any(LooksLikeUserToken))
        {
            await Write401Async(context,
                "Use the client token from POST /api/auth/client (X-Kinshout-Client-Token), not the user Bearer token.");
            return;
        }

        await Write401Async(context, "Invalid or expired frontend client token.");
    }

    private async Task<List<string>> CollectClientTokenCandidatesAsync(HttpContext context)
    {
        var candidates = new List<string>();

        if (context.Request.Headers.TryGetValue(AuthConstants.ClientTokenHeader, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            candidates.Add(header.ToString().Trim());
        }

        // IIS on Azure can corrupt X-Kinshout-Client-Token on multipart requests when Authorization is also set.
        if (context.Request.HasFormContentType)
        {
            context.Request.EnableBuffering();
            var form = await context.Request.ReadFormAsync();
            if (form.TryGetValue(AuthConstants.ClientTokenFormField, out var field)
                && !string.IsNullOrWhiteSpace(field))
            {
                var formToken = field.ToString().Trim();
                if (!candidates.Contains(formToken, StringComparer.Ordinal))
                    candidates.Add(formToken);
            }
        }

        if (context.Request.Query.TryGetValue(AuthConstants.ClientTokenQueryParam, out var query)
            && !string.IsNullOrWhiteSpace(query))
        {
            var queryToken = query.ToString().Trim();
            if (!candidates.Contains(queryToken, StringComparer.Ordinal))
                candidates.Add(queryToken);
        }

        return candidates;
    }

    private bool TryValidateClientToken(string token, out string clientId)
    {
        clientId = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, _clientValidation, out _);

            var tokenType = principal.FindFirst(AuthConstants.TokenTypeClaim)?.Value;
            clientId = principal.FindFirst(AuthConstants.ClientIdClaim)?.Value ?? "";
            return tokenType == AuthConstants.AppTokenType && !string.IsNullOrWhiteSpace(clientId);
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool LooksLikeUserToken(string token)
    {
        try
        {
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = token["Bearer ".Length..].Trim();

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var tokenType = jwt.Claims.FirstOrDefault(c => c.Type == AuthConstants.TokenTypeClaim)?.Value;
            return tokenType == AuthConstants.UserTokenType;
        }
        catch
        {
            return false;
        }
    }

    private static TokenValidationParameters BuildValidationParameters(JwtSettings jwt) =>
        new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.ClientAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
            ClockSkew = TimeSpan.FromMinutes(2),
        };

    private static Task Write401Async(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
