using Kinshout.Api.Auth;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

[ApiController]
[Route("api/editorial")]
public class EditorialController(IEditorialDeskService desk) : ControllerBase
{
    [HttpGet("gros-plan")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGrosPlan(CancellationToken ct)
    {
        var pin = await desk.GetGrosPlanAsync(ct);
        return Ok(new { discussionId = pin.DiscussionId, window = pin.Window });
    }

    [HttpPut("gros-plan")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    public async Task<IActionResult> SetGrosPlan([FromBody] GrosPlanRequest request, CancellationToken ct)
    {
        var userId = ControllerUserHelper.TryGetUserId(HttpContext);
        if (userId is null)
            return Unauthorized();

        try
        {
            var pin = await desk.SetGrosPlanAsync(userId.Value, request.DiscussionId, request.Window, ct);
            return Ok(new { discussionId = pin.DiscussionId, window = pin.Window });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("sources")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    public async Task<IActionResult> GetSources(CancellationToken ct)
    {
        var userId = ControllerUserHelper.TryGetUserId(HttpContext);
        if (userId is null)
            return Unauthorized();

        try
        {
            await desk.EnsureCanEditAsync(userId.Value, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }

        return Content(await desk.GetRegistryJsonAsync(ct), "application/json");
    }

    [HttpPut("sources")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    public async Task<IActionResult> SetSource([FromBody] SourceImportRequest request, CancellationToken ct)
    {
        var userId = ControllerUserHelper.TryGetUserId(HttpContext);
        if (userId is null)
            return Unauthorized();

        try
        {
            await desk.SetSourceImportAsync(userId.Value, request.Name, request.Import, ct);
            return Content(await desk.GetRegistryJsonAsync(ct), "application/json");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPost("sources")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    public async Task<IActionResult> AddSource([FromBody] AddSourceRequest request, CancellationToken ct)
    {
        var userId = ControllerUserHelper.TryGetUserId(HttpContext);
        if (userId is null)
            return Unauthorized();

        try
        {
            await desk.AddSourceAsync(
                userId.Value,
                new RegistrySourceInput(
                    request.Name,
                    request.CommunitySlug,
                    request.SearchQuery,
                    request.Mentions,
                    request.YoutubeChannelId,
                    request.YoutubeHandle,
                    request.Tiktok,
                    request.Facebook,
                    request.X,
                    request.HitsOnly),
                ct);
            return Content(await desk.GetRegistryJsonAsync(ct), "application/json");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    public sealed record GrosPlanRequest(Guid? DiscussionId, string? Window);
    public sealed record SourceImportRequest(string Name, bool Import);
    public sealed record AddSourceRequest(
        string Name,
        string CommunitySlug,
        string? SearchQuery,
        IReadOnlyList<string>? Mentions,
        string? YoutubeChannelId,
        string? YoutubeHandle,
        string? Tiktok,
        string? Facebook,
        string? X,
        bool HitsOnly);
}
