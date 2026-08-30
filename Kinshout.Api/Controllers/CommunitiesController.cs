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
    /// <summary>List Kinoiserie communities.</summary>
    /// <param name="sort">Sort order: <c>recent</c> (default) or <c>popular</c> (discussion count).</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<CommunityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResultDto<CommunityDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string sort = ListSortHelper.Recent,
        CancellationToken ct = default)
    {
        if (!ListSortHelper.TryNormalize(sort, out var normalizedSort))
            return BadRequest(new { error = "Le paramètre sort doit être recent ou popular." });

        return Ok(await communities.ListAsync(page, pageSize, normalizedSort, ct));
    }

    /// <summary>
    /// Preview which community fits a discussion draft (title + body) when none is selected yet.
    /// Uses OpenAI when configured; falls back to keyword matching.
    /// </summary>
    [HttpPost("preview")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SuggestCommunityResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SuggestCommunityResponseDto>> Preview(
        [FromBody] SuggestCommunityRequestDto request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await communities.SuggestAsync(request.Title, request.Body, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

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
