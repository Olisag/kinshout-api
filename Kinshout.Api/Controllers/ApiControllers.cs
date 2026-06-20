using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Controllers;

/// <summary>Published adverts — browse publicly, create when signed in.</summary>
[ApiController]
[Route("api/adverts")]
[Produces("application/json")]
public class AdvertsController(IAdvertService adverts) : ControllerBase
{
    /// <summary>
    /// List published adverts, optionally filtered by category.
    /// Requires frontend client token only.
    /// </summary>
    /// <param name="categoryId">Optional category GUID to filter results.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<AdvertDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdvertDto>>> List([FromQuery] Guid? categoryId, CancellationToken ct) =>
        Ok(await adverts.ListAsync(categoryId, ct));

    /// <summary>
    /// List adverts published by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpGet("mine")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<AdvertDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<AdvertDto>>> ListMine(CancellationToken ct) =>
        Ok(await adverts.ListMineAsync(GetUserId(), ct));

    /// <summary>
    /// Get a single advert by ID (title, price, location, category, tags, AI summary).
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AdvertDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdvertDto>> Get(Guid id, CancellationToken ct)
    {
        var advert = await adverts.GetByIdAsync(id, ct);
        return advert is null ? NotFound() : Ok(advert);
    }

    /// <summary>
    /// Publish a new advert. OpenAI categorizes the text and may create a new category dynamically.
    /// OpenAI moderation blocks sexual content and non-genuine/web-sourced photos.
    /// Requires client token + user JWT. User must have a WhatsApp number on their profile.
    /// Photos and CV are optional (up to 10 photos; CV for job-seeking adverts).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(AdvertDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AdvertDto>> Create([FromBody] CreateAdvertRequestDto request, CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            var advert = await adverts.CreateAsync(userId, request, ct);
            return CreatedAtAction(nameof(Get), new { id = advert.Id }, advert);
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
    /// Update an existing advert owned by the signed-in user.
    /// Requires client token + user JWT. Up to 10 photos; CV optional.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(AdvertDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdvertDto>> Update(
        Guid id,
        [FromBody] UpdateAdvertRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            var advert = await adverts.UpdateAsync(userId, id, request, ct);
            return Ok(advert);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
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

    private static Guid GetUserId(HttpContext http) =>
        Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());

    private Guid GetUserId() => GetUserId(HttpContext);
}

/// <summary>Categories — system-seeded and AI-created as adverts are posted.</summary>
[ApiController]
[Route("api/categories")]
[Produces("application/json")]
public class CategoriesController(KinshoutDbContext db) : ControllerBase
{
    /// <summary>
    /// List all categories (Immobilier, Emplois, etc., plus any created by OpenAI).
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> List(CancellationToken ct)
    {
        var items = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.IsSystem ? 0 : 1)
            .ThenBy(c => c.Label)
            .Select(c => new CategoryDto(c.Id, c.Slug, c.Label, c.Icon, c.IsAiGenerated))
            .ToListAsync(ct);
        return Ok(items);
    }
}

/// <summary>Semantic search — OpenAI matches adverts and discussions to the user's query.</summary>
[ApiController]
[Route("api/search")]
[Produces("application/json")]
public class SearchController(ISearchService search) : ControllerBase
{
    /// <summary>
    /// Top popular search queries (most searched first).
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet("popular")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<PopularSearchDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PopularSearchDto>>> Popular(
        [FromQuery] int limit = 10,
        CancellationToken ct = default) =>
        Ok(await search.GetPopularSearchesAsync(limit, ct));

    /// <summary>
    /// Record a search query for popularity stats (no search results returned).
    /// Requires frontend client token only.
    /// </summary>
    [HttpPost("record")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Record([FromBody] RecordSearchRequestDto request, CancellationToken ct)
    {
        await search.RecordSearchQueryAsync(request.Query, ct);
        return NoContent();
    }

    /// <summary>
    /// Search adverts and discussions using OpenAI semantic matching (POST body).
    /// Requires frontend client token only.
    /// </summary>
    /// <remarks>
    /// Set <c>tab</c> to <c>all</c>, <c>annonces</c>, or <c>discussions</c> to filter result types.
    /// Falls back to keyword matching if OpenAI is unavailable.
    /// </remarks>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SearchResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SearchResultDto>> Search([FromBody] SearchRequestDto request, CancellationToken ct) =>
        Ok(await search.SearchAsync(request, ct));

    /// <summary>
    /// Search adverts and discussions (query string). Same behaviour as POST /api/search.
    /// Requires frontend client token only.
    /// </summary>
    /// <param name="q">Search text, e.g. "appartement à Gombe".</param>
    /// <param name="tab">Result filter: all, annonces, or discussions.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SearchResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SearchResultDto>> SearchGet([FromQuery] string q, [FromQuery] string tab = "all", CancellationToken ct = default) =>
        Ok(await search.SearchAsync(new SearchRequestDto(q, tab), ct));
}

/// <summary>AI categorization preview — classify text without publishing an advert.</summary>
[ApiController]
[Route("api/categorize")]
[Produces("application/json")]
public class CategorizeController(ISearchService search) : ControllerBase
{
    /// <summary>
    /// Preview how OpenAI would categorize a text (category, intent, confidence, summary).
    /// May create a new category in the database if the AI suggests one.
    /// Requires frontend client token only.
    /// </summary>
    /// <remarks>
    /// Used by the publish flow before the user confirms. Does not create an advert.
    /// </remarks>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CategorizeResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CategorizeResponseDto>> Categorize([FromBody] CategorizeRequestDto request, CancellationToken ct) =>
        Ok(await search.CategorizeAsync(request.Text, ct));
}

/// <summary>Community discussions — browse publicly, post and reply when signed in.</summary>
[ApiController]
[Route("api/discussions")]
[Produces("application/json")]
public class DiscussionsController(IDiscussionService discussions) : ControllerBase
{
    /// <summary>
    /// List discussions, optionally filtered by a search query.
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<DiscussionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DiscussionDto>>> List([FromQuery] string? q, CancellationToken ct) =>
        Ok(await discussions.ListAsync(q, ct));

    /// <summary>
    /// Get a discussion with its full reply thread.
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DiscussionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var item = await discussions.GetByIdAsync(id, ct);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>
    /// Start a new discussion. OpenAI assigns a category.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(DiscussionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DiscussionDto>> Create([FromBody] CreateDiscussionRequestDto request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        var item = await discussions.CreateAsync(userId, request, ct);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    /// <summary>
    /// Reply to a discussion.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpPost("{id:guid}/replies")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(DiscussionReplyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionReplyDto>> Reply(Guid id, [FromBody] CreateReplyRequestDto request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        var reply = await discussions.AddReplyAsync(userId, id, request, ct);
        return Ok(reply);
    }
}

/// <summary>Service health check — no authentication required.</summary>
[ApiController]
[Route("api/health")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Returns API status. Used for uptime monitoring and deployment checks.
    /// Does not require client or user tokens.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new { status = "ok", service = "kinshout-api", version = "1.0.0" });
}
