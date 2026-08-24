using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>Kinoiserie communities — public route form is <c>k/{slug}</c>.</summary>
[ApiController]
[Route("api/communities")]
[Produces("application/json")]
public class CommunitiesController(ICommunityService communities) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<CommunityDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<CommunityDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await communities.ListAsync(page, pageSize, ct));

    [HttpGet("{slug}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CommunityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommunityDto>> Get(string slug, CancellationToken ct)
    {
        try
        {
            var item = await communities.GetBySlugAsync(slug, ct);
            return item is null ? NotFound(new { error = "Communauté introuvable." }) : Ok(item);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(CommunityDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CommunityDto>> Create(
        [FromBody] CreateCommunityRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var created = await communities.CreateAsync(GetUserId(), request, ct);
            return CreatedAtAction(nameof(Get), new { slug = created.Slug }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{slug}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string slug, CancellationToken ct)
    {
        try
        {
            await communities.DeleteAsync(GetUserId(), slug, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Communauté introuvable." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());
}
