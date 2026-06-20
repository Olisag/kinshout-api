using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>File uploads for advert photos and resumes.</summary>
[ApiController]
[Route("api/uploads")]
[Authorize(Policy = AuthConstants.UserPolicy)]
public class UploadsController(IUploadService uploads) : ControllerBase
{
    /// <summary>
    /// Upload one or more advert photos (optional, max 10). OpenAI verifies authenticity and blocks sexual or web-sourced images.
    /// Returns public URLs for use in <c>POST /api/adverts</c>.
    /// </summary>
    [HttpPost("images")]
    [RequestSizeLimit(52_428_800)]
    [ProducesResponseType(typeof(UploadResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadResponseDto>> UploadImages(CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            if (Request.Form.Files.Count == 0)
                return BadRequest(new { error = "Aucune image reçue." });

            var urls = await uploads.SaveImagesAsync(userId, Request.Form.Files, ct);
            return Ok(new UploadResponseDto(urls));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (AdvertModerationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Upload a resume/CV (optional, for job-seeking adverts). Returns a public URL for use in <c>POST /api/adverts</c>.
    /// </summary>
    [HttpPost("resume")]
    [RequestSizeLimit(10_485_760)]
    [ProducesResponseType(typeof(UploadResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadResponseDto>> UploadResume(CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            var file = Request.Form.Files.FirstOrDefault();
            if (file is null)
                return BadRequest(new { error = "Aucun CV reçu." });

            var url = await uploads.SaveResumeAsync(userId, file, ct);
            return Ok(new UploadResponseDto([url]));
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
