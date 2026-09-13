using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>
/// Reddit-style videos: upload once, cheap poster preview for feeds, range-streamed playback, owner delete.
/// Avoids paid cloud transcoder fees — original file + optional poster only.
/// </summary>
[ApiController]
[Route("api/videos")]
[Produces("application/json")]
public class VideosController(IVideoService videos) : ControllerBase
{
    /// <summary>
    /// Upload a video (mp4/webm/mov, max 50MB) with an optional poster image for feed previews.
    /// Returns play/preview URLs. Attach <c>playUrl</c> or the underlying storage path when posting discussions.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(262_144_000)]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VideoDto>> Upload(
        IFormFile file,
        IFormFile? poster = null,
        CancellationToken ct = default)
    {
        try
        {
            var created = await videos.UploadAsync(GetUserId(), file, poster, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get video metadata (play URL + optional preview URL). Anonymous OK.</summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VideoDto>> Get(Guid id, CancellationToken ct)
    {
        var item = await videos.GetAsync(id, ct);
        return item is null ? NotFound(new { error = "Vidéo introuvable." }) : Ok(item);
    }

    /// <summary>
    /// Serve the cheap poster image for feeds/lists (long-cache). Prefer this over loading the video.
    /// </summary>
    [HttpGet("{id:guid}/preview")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        var file = await videos.OpenPreviewAsync(id, ct);
        if (file is null)
            return NotFound(new { error = "Aperçu introuvable." });

        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(file.Stream, file.ContentType);
    }

    /// <summary>
    /// Stream the video with HTTP range support for progressive playback (seek without full download).
    /// </summary>
    [HttpGet("{id:guid}/stream")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stream(Guid id, CancellationToken ct)
    {
        var file = await videos.OpenStreamAsync(id, ct);
        if (file is null)
            return NotFound(new { error = "Vidéo introuvable." });

        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(file.Stream, file.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Delete a video and its poster. Owner only.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await videos.DeleteAsync(GetUserId(), id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Vidéo introuvable." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());
}
