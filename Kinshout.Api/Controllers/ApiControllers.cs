using System.Security.Claims;
using Kinshout.Api.Auth;
using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Controllers;

/// <summary>Published adverts — browse publicly, create when signed in.</summary>
[ApiController]
[Route("api/adverts")]
[Produces("application/json")]
public class AdvertsController(IAdvertService adverts, ISavedAdvertService savedAdverts) : ControllerBase
{
    /// <summary>
    /// List published adverts, optionally filtered by category.
    /// Requires frontend client token only.
    /// </summary>
    /// <param name="categoryId">Optional category GUID to filter results.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Items per page (max 50).</param>
    /// <param name="sort">Sort order: <c>recent</c> (default) or <c>popular</c> (view count).</param>
    /// <param name="intent">Optional filter: <c>offre</c> or <c>demande</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<AdvertDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResultDto<AdvertDto>>> List(
        [FromQuery] Guid? categoryId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string sort = "recent",
        [FromQuery] string? intent = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(intent))
        {
            var normalized = intent.Trim().ToLowerInvariant();
            if (normalized is not ("offre" or "demande"))
                return BadRequest(new { error = "Le paramètre intent doit être offre ou demande." });
            intent = normalized;
        }

        try
        {
            return Ok(await adverts.ListAsync(categoryId, page, pageSize, sort, intent, TryGetUserId(), ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List adverts published by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpGet("mine")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(PagedResultDto<AdvertDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResultDto<AdvertDto>>> ListMine(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await adverts.ListMineAsync(GetUserId(), page, pageSize, ct));

    /// <summary>
    /// List advert IDs saved by the signed-in user (for heart button state).
    /// Requires client token + user JWT.
    /// </summary>
    [HttpGet("saved/ids")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<Guid>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<Guid>>> ListSavedIds(CancellationToken ct) =>
        Ok(await savedAdverts.ListSavedIdsAsync(GetUserId(), ct));

    /// <summary>
    /// List adverts saved by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpGet("saved")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(PagedResultDto<AdvertDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResultDto<AdvertDto>>> ListSaved(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await savedAdverts.ListSavedAsync(GetUserId(), page, pageSize, ct));

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
        var advert = await adverts.GetByIdAsync(id, TryGetUserId(), ct);
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

    /// <summary>
    /// Permanently delete an advert owned by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await adverts.DeleteAsync(GetUserId(), id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Save an advert to the signed-in user's favorites.
    /// Returns the updated advert with <c>isSaved</c> and <c>likeCount</c>.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpPost("{id:guid}/save")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(AdvertDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdvertDto>> Save(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await savedAdverts.SaveAsync(GetUserId(), id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Remove an advert from the signed-in user's favorites.
    /// Returns the updated advert with <c>isSaved</c> and <c>likeCount</c>.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpDelete("{id:guid}/save")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(AdvertDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdvertDto>> Unsave(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await savedAdverts.UnsaveAsync(GetUserId(), id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static Guid GetUserId(HttpContext http) =>
        Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());

    private Guid GetUserId() => GetUserId(HttpContext);

    private Guid? TryGetUserId() => ControllerUserHelper.TryGetUserId(HttpContext);
}

/// <summary>Categories — system-seeded and AI-created as adverts are posted.</summary>
[ApiController]
[Route("api/categories")]
[Produces("application/json")]
public class CategoriesController(KinshoutDbContext db, IMemoryCache cache) : ControllerBase
{
    private static readonly TimeSpan CategoriesCacheDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// List advert categories (Immobilier, Emplois, etc., plus any created by OpenAI).
    /// Excludes the Discussions category — use <c>/api/discussions</c> for forum content.
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<CategoryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<CategoryDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var cacheKey = $"{ApiCacheKeys.CategoriesAll}:{normalizedPage}:{normalizedPageSize}";

        var result = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CategoriesCacheDuration;

            var query = db.Categories
                .AsNoTracking()
                .Where(c => c.Slug != Category.DiscussionSlug)
                .OrderBy(c => c.IsSystem ? 0 : 1)
                .ThenBy(c => c.Label);

            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .Select(c => new CategoryDto(c.Id, c.Slug, c.Label, c.Icon, c.IsAiGenerated))
                .ToListAsync(ct);

            return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
        });

        return Ok(result ?? PagingHelper.Create(Array.Empty<CategoryDto>(), normalizedPage, normalizedPageSize, 0));
    }
}

/// <summary>Semantic search — OpenAI matches adverts and discussions to the user's query.</summary>
[ApiController]
[Route("api/search")]
[Produces("application/json")]
public class SearchController(ISearchService search) : ControllerBase
{
    /// <summary>
    /// Popular search queries (most searched first).
    /// Requires frontend client token only.
    /// </summary>
    [HttpGet("popular")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<PopularSearchDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<PopularSearchDto>>> Popular(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await search.GetPopularSearchesAsync(page, pageSize, ct));

    /// <summary>
    /// Search adverts and discussions using OpenAI semantic matching (POST body).
    /// Records the query for popularity stats.
    /// Requires frontend client token only.
    /// </summary>
    /// <remarks>
    /// Set <c>tab</c> to <c>all</c>, <c>annonces</c>, or <c>discussions</c> to filter result types.
    /// Use <c>sort</c> (<c>recent</c> or <c>popular</c>) and optional <c>intent</c> (<c>demande</c>, <c>offre</c>, <c>discussion</c>).
    /// Use <c>page</c> (1-based) and <c>pageSize</c> (max 50, default 20) for pagination.
    /// Falls back to keyword matching if OpenAI is unavailable.
    /// </remarks>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SearchResultDto>> Search([FromBody] SearchRequestDto request, CancellationToken ct)
    {
        if (!TryNormalizeSearchParams(request.Sort, request.Intent, out var sort, out var intent, out var error))
            return BadRequest(new { error });

        return Ok(await search.SearchAsync(
            request with { Sort = sort, Intent = intent },
            ControllerUserHelper.TryGetUserId(HttpContext),
            ct));
    }

    /// <summary>
    /// Search adverts and discussions (query string). Same behaviour as POST /api/search.
    /// Records the query for popularity stats.
    /// Requires frontend client token only.
    /// </summary>
    /// <param name="q">Search text, e.g. "appartement à Gombe".</param>
    /// <param name="tab">Result filter: all, annonces, or discussions.</param>
    /// <param name="sort">Sort order: recent (default) or popular.</param>
    /// <param name="intent">Optional intent filter: demande, offre, or discussion.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Results per type per page (max 50).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SearchResultDto>> SearchGet(
        [FromQuery] string q,
        [FromQuery] string tab = "all",
        [FromQuery] string sort = "recent",
        [FromQuery] string? intent = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryNormalizeSearchParams(sort, intent, out sort, out intent, out var error))
            return BadRequest(new { error });

        return Ok(await search.SearchAsync(
            new SearchRequestDto(q, tab, page, pageSize, sort, intent),
            ControllerUserHelper.TryGetUserId(HttpContext),
            ct));
    }

    private static bool TryNormalizeSearchParams(
        string? sort,
        string? intent,
        out string normalizedSort,
        out string? normalizedIntent,
        out string error)
    {
        if (!ListSortHelper.TryNormalize(sort, out normalizedSort))
        {
            error = "Le paramètre sort doit être recent ou popular.";
            normalizedIntent = null;
            return false;
        }

        if (!SearchIntentHelper.TryNormalize(intent, out normalizedIntent))
        {
            error = "Le paramètre intent doit être demande, offre ou discussion.";
            return false;
        }

        error = "";
        return true;
    }
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
public class DiscussionsController(IDiscussionService discussions, ILikedDiscussionService likedDiscussions) : ControllerBase
{
    /// <summary>
    /// List discussions, optionally filtered by a search query.
    /// Requires frontend client token only.
    /// </summary>
    /// <param name="q">Optional text filter on title or body.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Items per page (max 50).</param>
    /// <param name="sort">Sort order: <c>recent</c> (default) or <c>popular</c> (view count).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResultDto<DiscussionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<DiscussionDto>>> List(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string sort = "recent",
        CancellationToken ct = default) =>
        Ok(await discussions.ListAsync(q, page, pageSize, sort, TryGetUserId(), ct));

    /// <summary>
    /// List discussion IDs liked by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpGet("liked/ids")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<Guid>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<Guid>>> ListLikedIds(CancellationToken ct) =>
        Ok(await likedDiscussions.ListLikedIdsAsync(GetUserId(), ct));

    /// <summary>
    /// List discussions for the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    /// <param name="filter">
    /// <c>all</c> (default) — started or replied;
    /// <c>authored</c> — started by the user;
    /// <c>replies</c> — replied to (excluding own threads).
    /// </param>
    [HttpGet("mine")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(PagedResultDto<DiscussionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResultDto<DiscussionDto>>> ListMine(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string filter = "all",
        CancellationToken ct = default) =>
        Ok(await discussions.ListMineAsync(GetUserId(), page, pageSize, filter, ct));

    /// <summary>
    /// Get a discussion with a paginated reply thread (oldest replies first).
    /// Requires frontend client token only.
    /// </summary>
    /// <param name="id">Discussion ID.</param>
    /// <param name="page">Reply page number (1-based).</param>
    /// <param name="pageSize">Replies per page (max 50).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DiscussionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionDetailDto>> Get(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var item = await discussions.GetByIdAsync(id, page, pageSize, TryGetUserId(), ct);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>
    /// Like a discussion. Requires client token + user JWT.
    /// </summary>
    [HttpPost("{id:guid}/like")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(DiscussionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionDto>> Like(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await likedDiscussions.LikeAsync(GetUserId(), id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Remove a like from a discussion. Requires client token + user JWT.
    /// </summary>
    [HttpDelete("{id:guid}/like")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(DiscussionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionDto>> Unlike(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await likedDiscussions.UnlikeAsync(GetUserId(), id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
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
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var item = await discussions.CreateAsync(userId, request, ct);
            return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
        }
        catch (AdvertModerationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var reply = await discussions.AddReplyAsync(userId, id, request, ct);
            return Ok(reply);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (AdvertModerationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a discussion started by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(DiscussionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionDetailDto>> Update(
        Guid id,
        [FromBody] UpdateDiscussionRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            var item = await discussions.UpdateAsync(userId, id, request, ct);
            return Ok(item);
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

    /// <summary>
    /// Permanently delete a discussion started by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await discussions.DeleteAsync(GetUserId(), id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Update a reply written by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpPut("{id:guid}/replies/{replyId:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(typeof(DiscussionReplyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionReplyDto>> UpdateReply(
        Guid id,
        Guid replyId,
        [FromBody] UpdateReplyRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            var reply = await discussions.UpdateReplyAsync(userId, id, replyId, request, ct);
            return Ok(reply);
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

    /// <summary>
    /// Permanently delete a reply written by the signed-in user.
    /// Requires client token + user JWT.
    /// </summary>
    [HttpDelete("{id:guid}/replies/{replyId:guid}")]
    [Authorize(Policy = AuthConstants.UserPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteReply(Guid id, Guid replyId, CancellationToken ct)
    {
        try
        {
            await discussions.DeleteReplyAsync(GetUserId(), id, replyId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException());

    private Guid? TryGetUserId() => ControllerUserHelper.TryGetUserId(HttpContext);
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
