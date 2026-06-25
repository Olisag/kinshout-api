using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>Public user profiles — visible only when the user opts in.</summary>
[ApiController]
[Route("api/users")]
[Produces("application/json")]
public class UsersController(IUserProfileService profiles) : ControllerBase
{
    /// <summary>
    /// View a user's public profile (name, avatar, member since, advert count).
    /// Requires frontend client token. Returns 404 if the profile is private.
    /// </summary>
    [HttpGet("{id:guid}/profile")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PublicUserProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicUserProfileDto>> GetProfile(Guid id, CancellationToken ct)
    {
        var profile = await profiles.GetPublicProfileAsync(id, ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    /// <summary>
    /// List published adverts for a user with a public profile.
    /// Requires frontend client token. Returns 404 if the profile is private.
    /// </summary>
    [HttpGet("{id:guid}/adverts")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<AdvertDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<AdvertDto>>> ListAdverts(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await profiles.ListPublicAdvertsAsync(id, page, pageSize, ControllerUserHelper.TryGetUserId(HttpContext), ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
