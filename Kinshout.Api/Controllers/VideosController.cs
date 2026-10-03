using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kinshout.Api.Auth;
using Kinshout.Api.Configuration;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Controllers;

/// <summary>
/// Videos: direct upload to storage, server-side trim (two minutes max) and 720p re-encode,
/// cheap poster preview for feeds, range-streamed playback, owner delete.
/// </summary>
[ApiController]
[Route("api/videos")]
[Produces("application/json")]
public class VideosController(
    IVideoService videos,
    IOptions<VideoProcessingSettings>? processingOptions = null) : ControllerBase
{
    public const string WorkerKeyHeader = "X-Video-Worker-Key";

    /// <summary>
    /// Start a direct upload. The client PUTs the source file to <c>uploadUrl</c> (Azure block blob API),
    /// then calls <c>POST /api/videos/{id}/complete</c>. 501 when the storage cannot issue upload URLs:
    /// fall back to <c>POST /api/videos</c>.
    /// </summary>
    [HttpPost("uploads")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(VideoUploadDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult<VideoUploadDto>> CreateUpload(
        [FromBody] CreateVideoUploadRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var upload = await videos.CreateUploadAsync(GetUserId(), request, ct);
            if (upload is null)
                return StatusCode(StatusCodes.Status501NotImplemented, new { error = "Envoi direct indisponible." });

            return CreatedAtAction(nameof(Get), new { id = upload.Id }, upload);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Finish a direct upload: optional trim range (seconds, two minutes max) and poster image.
    /// Poll <c>GET /api/videos/{id}</c> until <c>status</c> is <c>ready</c> or <c>failed</c>.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_097_152)]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VideoDto>> CompleteUpload(
        Guid id,
        [FromForm] double? trimStart,
        [FromForm] double? trimEnd,
        IFormFile? poster,
        CancellationToken ct)
    {
        try
        {
            return Ok(await videos.CompleteUploadAsync(GetUserId(), id, trimStart, trimEnd, poster, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
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

    /// <summary>Video worker callback. Authenticated with the shared worker key, not a user token.</summary>
    [HttpPost("{id:guid}/processing-result")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> ProcessingResult(
        Guid id,
        [FromBody] VideoProcessingResultDto result,
        CancellationToken ct)
    {
        if (!IsWorkerRequest())
            return Unauthorized();

        return await videos.ApplyProcessingResultAsync(id, result, ct)
            ? NoContent()
            : NotFound(new { error = "Vidéo introuvable." });
    }
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
    /// Play the video. Redirects to a short-lived storage URL when available so range requests go
    /// straight to blob storage; otherwise streams with HTTP range support (seek without full download).
    /// </summary>
    [HttpGet("{id:guid}/stream")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stream(Guid id, CancellationToken ct)
    {
        var directUri = await videos.GetDirectStreamUriAsync(id, ct);
        if (directUri is not null)
        {
            // Must expire well before the signed URL does.
            Response.Headers.CacheControl = "public,max-age=3600";
            return Redirect(directUri.AbsoluteUri);
        }

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

    private bool IsWorkerRequest()
    {
        var expected = processingOptions?.Value.WorkerKey;
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        var provided = Request.Headers[WorkerKeyHeader].ToString();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());
}
