using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>Authentication — frontend client (layer 1) and end-user sign-in (layer 2).</summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(IAuthService auth, IClientAuthService clientAuth) : ControllerBase
{
    /// <summary>
    /// Authorize a registered frontend app (layer 1).
    /// Send <c>clientId</c> + <c>clientSecret</c> from the frontend; returns a client JWT
    /// sent as <c>X-Kinshout-Client-Token</c> on all other API calls.
    /// </summary>
    /// <remarks>
    /// Validates the request <c>Origin</c> against the client's allowed origins list.
    /// </remarks>
    [HttpPost("client")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ClientAuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ClientAuthResponseDto>> Client([FromBody] ClientAuthRequestDto request, CancellationToken ct)
    {
        try
        {
            var origin = OriginMatcher.NormalizeOrigin(Request.Headers.Origin.FirstOrDefault())
                ?? OriginMatcher.NormalizeOrigin(Request.Headers.Referer.FirstOrDefault());
            return Ok(await clientAuth.AuthenticateAsync(request, origin, ct));
        }
        catch (Exception ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sign in an end user with a Google ID token (layer 2).
    /// Requires a valid frontend client token in <c>X-Kinshout-Client-Token</c>.
    /// </summary>
    /// <remarks>
    /// Creates or links the user account, then returns a user JWT for protected routes (post advert, profile, etc.).
    /// </remarks>
    [HttpPost("google")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponseDto>> Google([FromBody] ExternalLoginRequestDto request, CancellationToken ct)
    {
        try
        {
            var clientId = GetClientId();
            return Ok(await auth.SignInWithGoogleAsync(request.IdToken, clientId, ct));
        }
        catch (Exception ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sign in an end user with an Apple identity token (layer 2).
    /// Requires a valid frontend client token in <c>X-Kinshout-Client-Token</c>.
    /// </summary>
    /// <remarks>
    /// Creates or links the user account, then returns a user JWT for protected routes.
    /// </remarks>
    [HttpPost("apple")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponseDto>> Apple([FromBody] ExternalLoginRequestDto request, CancellationToken ct)
    {
        try
        {
            var clientId = GetClientId();
            return Ok(await auth.SignInWithAppleAsync(request.IdToken, clientId, ct));
        }
        catch (Exception ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get the signed-in user's profile.
    /// Requires both client token and user JWT.
    /// </summary>
    [HttpGet("me")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserProfileDto>> Me(CancellationToken ct)
    {
        var userId = GetUserId();
        var profile = await auth.GetProfileAsync(userId, ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    /// <summary>
    /// Update the signed-in user's profile. WhatsApp number is required to publish adverts.
    /// Requires both client token and user JWT.
    /// </summary>
    [HttpPatch("me")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserProfileDto>> UpdateMe(
        [FromBody] UpdateProfileRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            return Ok(await auth.UpdateProfileAsync(userId, request, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private string GetClientId() =>
        HttpContext.Items[AuthConstants.ClientContextKey]?.ToString()
        ?? throw new UnauthorizedAccessException("Frontend client context is required.");

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());
}
