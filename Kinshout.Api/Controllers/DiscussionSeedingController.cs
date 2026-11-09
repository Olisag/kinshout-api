using Kinshout.Api.Auth;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>Operations controls, restricted to the same moderators as the editorial desk.</summary>
[ApiController]
[Authorize(Policy = AuthConstants.UserPolicy)]
[Route("api/editorial/discussion-seeding")]
public sealed class DiscussionSeedingController(
    IDiscussionSeedingService seeding,
    IEditorialDeskService desk) : ControllerBase
{
    [HttpGet("jobs")]
    public Task<IActionResult> ListJobs([FromQuery] int limit = 50, CancellationToken ct = default) =>
        AsModeratorAsync(async _ => Ok(await seeding.ListJobsAsync(limit, ct)), ct);

    [HttpGet("flags")]
    public Task<IActionResult> GetFlags(CancellationToken ct) =>
        AsModeratorAsync(async _ => Ok(await seeding.GetFlagsAsync(ct)), ct);

    [HttpPut("flags")]
    public Task<IActionResult> SetFlags([FromBody] DiscussionSeedingFlags flags, CancellationToken ct) =>
        AsModeratorAsync(async userId => Ok(await seeding.SetFlagsAsync(userId, flags, ct)), ct);

    [HttpPost("runs")]
    public Task<IActionResult> CreateRun([FromBody] CreateRunRequest request, CancellationToken ct) =>
        AsModeratorAsync(async userId => Ok(await seeding.CreateRunAsync(userId, request.Name, ct)), ct);

    [HttpGet("rollback-preview")]
    public Task<IActionResult> PreviewRollback([FromQuery] Guid? runId, CancellationToken ct) =>
        AsModeratorAsync(async _ => Ok(await seeding.PreviewRollbackAsync(runId, ct)), ct);

    [HttpPost("stop-and-hide")]
    public Task<IActionResult> StopAndHide([FromBody] StopRequest request, CancellationToken ct) =>
        AsModeratorAsync(async userId => Ok(await seeding.StopAndHideAsync(userId, request.RunId, ct)), ct);

    private async Task<IActionResult> AsModeratorAsync(Func<Guid, Task<IActionResult>> action, CancellationToken ct)
    {
        var userId = ControllerUserHelper.TryGetUserId(HttpContext);
        if (userId is null)
            return Unauthorized();
        try
        {
            await desk.EnsureCanEditAsync(userId.Value, ct);
            return await action(userId.Value);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    public sealed record CreateRunRequest(string Name);
    public sealed record StopRequest(Guid? RunId = null);
}
