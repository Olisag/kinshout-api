using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>Public user profiles — visible only when the user opts in.</summary>
[ApiController]
[Route("api/users")]
[Produces("application/json")]
public class UsersController(
    IUserProfileService profiles,
    IDiscussionService discussions,
    IUserBlockService userBlocks) : ControllerBase
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
        var profile = await profiles.GetPublicProfileAsync(id, ControllerUserHelper.TryGetUserId(HttpContext), ct);
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

    /// <summary>
    /// List public discussions started by a user with a public profile.
    /// Requires frontend client token. Returns 404 if the profile is private.
    /// </summary>
    [HttpGet("{id:guid}/discussions")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<DiscussionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<DiscussionDto>>> ListDiscussions(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await discussions.ListPublicByUserAsync(
                id,
                page,
                pageSize,
                ControllerUserHelper.TryGetUserId(HttpContext),
                ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Personally block another user. Neither party sees the other's discussions or adverts.
    /// </summary>
    [HttpPost("{id:guid}/block")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BlockUser(Guid id, CancellationToken ct)
    {
        try
        {
            await userBlocks.BlockAsync(GetUserId(), id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Utilisateur introuvable." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Remove a personal block.</summary>
    [HttpDelete("{id:guid}/block")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnblockUser(Guid id, CancellationToken ct)
    {
        await userBlocks.UnblockAsync(GetUserId(), id, ct);
        return NoContent();
    }

    /// <summary>List users you have personally blocked.</summary>
    [HttpGet("me/blocks")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(PagedResultDto<UserBlockDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<UserBlockDto>>> ListMyBlocks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await userBlocks.ListMineAsync(GetUserId(), page, pageSize, ct));

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());
}
