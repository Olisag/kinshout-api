using Kinshout.Api.Configuration;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Controllers;

/// <summary>Import external adverts from approved ingestion pipelines (not user-facing).</summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/imports")]
[Produces("application/json")]
public class ImportsController(
    IExternalAdvertImportService imports,
    IExternalDiscussionImportService discussionImports,
    IOptions<ImportSettings> importOptions) : ControllerBase
{
    /// <summary>
    /// Upsert adverts from external marketplaces (batch).
    /// </summary>
    /// <remarks>
    /// Requires header <c>X-Kinshout-Import-Key</c> matching <c>Import:SecretKey</c> in app settings.
    ///
    /// Each item includes a <c>source</c> object. <c>source.provider</c> must be one of:
    /// <c>facebook_marketplace</c>, <c>mediacongo</c>, <c>zwandako</c>, <c>jiji_rdc</c>, or <c>other</c>.
    /// <c>source.externalId</c> + <c>source.provider</c> identify duplicates across runs.
    ///
    /// Response counts: <c>created</c>, <c>updated</c>, <c>unchanged</c>, <c>skipped</c> (validation/DB errors per row).
    /// </remarks>
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
    /// List <c>provider</c> + <c>externalId</c> pairs already stored (incremental import helper).
    /// </summary>
    /// <remarks>
    /// Requires header <c>X-Kinshout-Import-Key</c>. Used by the external importer to skip listings already in Kinshout.
    /// </remarks>
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

    /// <summary>
    /// Upsert discussion topics from external social sources (batch).
    /// </summary>
    [HttpPost("discussions")]
    [ProducesResponseType(typeof(ImportExternalDiscussionsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ImportExternalDiscussionsResponseDto>> ImportDiscussions(
        [FromBody] ImportExternalDiscussionsRequestDto request,
        CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized(new { error = "Clé d'import invalide." });

        if (request.Discussions.Count == 0)
            return BadRequest(new { error = "Aucune discussion à importer." });

        try
        {
            return Ok(await discussionImports.ImportAsync(request.Discussions, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List <c>provider</c> + <c>externalId</c> pairs for imported discussions.
    /// </summary>
    [HttpGet("known-discussions")]
    [ProducesResponseType(typeof(ImportKnownDiscussionsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ImportKnownDiscussionsResponseDto>> GetKnownDiscussions(CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized(new { error = "Clé d'import invalide." });

        var discussions = await discussionImports.GetKnownDiscussionKeysAsync(ct);
        return Ok(new ImportKnownDiscussionsResponseDto(discussions));
    }

    /// <summary>
    /// Last successful discussion import run per provider (incremental weekly fetch helper).
    /// </summary>
    [HttpGet("discussion-import-state")]
    [ProducesResponseType(typeof(DiscussionImportStateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DiscussionImportStateResponseDto>> GetDiscussionImportState(CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized(new { error = "Clé d'import invalide." });

        var providers = await discussionImports.GetDiscussionImportStateAsync(ct);
        return Ok(new DiscussionImportStateResponseDto(providers));
    }

    /// <summary>
    /// Record a completed discussion import run for a provider.
    /// </summary>
    [HttpPost("discussion-import-runs")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecordDiscussionImportRun(
        [FromBody] RecordDiscussionImportRunRequestDto request,
        CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized(new { error = "Clé d'import invalide." });

        if (string.IsNullOrWhiteSpace(request.Provider))
            return BadRequest(new { error = "provider requis." });

        try
        {
            await discussionImports.RecordDiscussionImportRunAsync(
                request.Provider,
                request.RunAt ?? DateTime.UtcNow,
                ct);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
