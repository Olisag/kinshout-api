using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinshout.Api.Controllers;

/// <summary>Serves uploaded images and documents from local disk or Azure Blob Storage.</summary>
[ApiController]
[Route("uploads")]
public class UploadedFilesController(IUploadStorage storage) : ControllerBase
{
    /// <summary>
    /// Get an uploaded file by its public path (e.g. /uploads/images/{userId}/{file}.png).
    /// No authentication required.
    /// </summary>
    [HttpGet("{**path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string path, CancellationToken ct)
    {
        var uploadUrl = $"/uploads/{path}";
        var file = await storage.OpenReadAsync(uploadUrl, ct);
        if (file is null)
            return NotFound();

        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(file.Stream, file.ContentType, enableRangeProcessing: true);
    }
}
