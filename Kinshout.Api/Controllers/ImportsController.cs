using Kinshout.Api.Configuration;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Controllers;

/// <summary>Import external adverts from approved ingestion pipelines (not user-facing).</summary>
[ApiController]
[Route("api/imports")]
[Produces("application/json")]
public class ImportsController(
    IExternalAdvertImportService imports,
    IOptions<ImportSettings> importOptions) : ControllerBase
{
    /// <summary>
    /// Upsert adverts from external marketplaces.
    /// Requires <c>X-Kinshout-Import-Key</c> header matching configured Import:SecretKey.
    /// </summary>
    [HttpPost("adverts")]
    [ProducesResponseType(typeof(ImportExternalAdvertsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ImportExternalAdvertsResponseDto>> ImportAdverts(
        [FromBody] ImportExternalAdvertsRequestDto request,
        CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized(new { error = "Clé d'import invalide." });

        if (request.Adverts.Count == 0)
            return BadRequest(new { error = "Aucune annonce à importer." });

        try
        {
            return Ok(await imports.ImportAsync(request.Adverts, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List provider/external-id pairs already stored (for incremental imports).
    /// Requires <c>X-Kinshout-Import-Key</c> header.
    /// </summary>
    [HttpGet("known-adverts")]
    [ProducesResponseType(typeof(ImportKnownAdvertsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ImportKnownAdvertsResponseDto>> GetKnownAdverts(CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized(new { error = "Clé d'import invalide." });

        var adverts = await imports.GetKnownAdvertKeysAsync(ct);
        return Ok(new ImportKnownAdvertsResponseDto(adverts));
    }

    private bool IsAuthorized()
    {
        var configured = importOptions.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(configured))
            return false;

        if (!Request.Headers.TryGetValue("X-Kinshout-Import-Key", out var provided))
            return false;

        return string.Equals(provided.ToString(), configured, StringComparison.Ordinal);
    }
}
