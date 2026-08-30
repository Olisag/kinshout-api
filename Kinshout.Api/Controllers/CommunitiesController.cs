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

        return Ok(await communities.ListAsync(page, pageSize, normalizedSort, TryGetUserId(), ct));
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
            var item = await communities.GetBySlugAsync(slug, TryGetUserId(), ct);
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

    /// <summary>
    /// Join a community. Public communities grant access immediately; private communities
    /// create a pending request and email the creator and moderators.
    /// Any one creator or moderator approval is sufficient.
    /// </summary>
    [HttpPost("{slug}/join")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Join(string slug, CancellationToken ct)
    {
        try
        {
            await communities.RequestJoinAsync(GetUserId(), slug, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Communauté introuvable." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{slug}/invite")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Invite(
        string slug,
        [FromBody] InviteCommunityMemberRequestDto request,
        CancellationToken ct)
    {
        try
        {
            await communities.InviteMemberAsync(GetUserId(), slug, request.UserId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{slug}/members/pending")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(PagedResultDto<CommunityMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<CommunityMemberDto>>> ListPendingMembers(
        string slug,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await communities.ListPendingMembersAsync(GetUserId(), slug, page, pageSize, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Communauté introuvable." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Approve a pending community join request. Any one creator or moderator approval suffices.
    /// </summary>
    [HttpPost("{slug}/members/{userId:guid}/approve")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveMember(string slug, Guid userId, CancellationToken ct)
    {
        try
        {
            await communities.ApproveMemberAsync(GetUserId(), slug, userId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{slug}/members/{userId:guid}/reject")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RejectMember(string slug, Guid userId, CancellationToken ct)
    {
        try
        {
            await communities.RejectMemberAsync(GetUserId(), slug, userId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{slug}/members/{userId:guid}/moderator")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddModerator(string slug, Guid userId, CancellationToken ct)
    {
        try
        {
            await communities.AddModeratorAsync(GetUserId(), slug, userId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{slug}/members/{userId:guid}/moderator")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveModerator(string slug, Guid userId, CancellationToken ct)
    {
        try
        {
            await communities.RemoveModeratorAsync(GetUserId(), slug, userId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{slug}/leave")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Leave(string slug, CancellationToken ct)
    {
        try
        {
            await communities.LeaveAsync(GetUserId(), slug, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Communauté introuvable." });
        }
        catch (InvalidOperationException ex)
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

    private Guid? TryGetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
