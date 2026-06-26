using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kinshout.Api.Services;

public interface ISearchService
{
    Task<SearchResultDto> SearchAsync(SearchRequestDto request, Guid? viewerUserId = null, CancellationToken ct = default);
    Task<CategorizeResponseDto> CategorizeAsync(string text, CancellationToken ct = default);
    Task<PagedResultDto<PopularSearchDto>> GetPopularSearchesAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
}

public class SearchService(KinshoutDbContext db, IOpenAiService openAi, IMemoryCache cache) : ISearchService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private static readonly TimeSpan PopularSearchesCacheDuration = TimeSpan.FromSeconds(30);

    public async Task<SearchResultDto> SearchAsync(SearchRequestDto request, Guid? viewerUserId = null, CancellationToken ct = default)
    {
        var query = request.Query.Trim();
        var (page, pageSize) = NormalizePaging(request.Page, request.PageSize);

        if (page == 1)
            await RecordSearchQueryAsync(query, ct);

        var adverts = await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.IsPublished)
            .OrderByDescending(a => a.ViewCount)
            .ThenByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var discussions = await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .OrderByDescending(d => d.ReplyCount)
            .ThenByDescending(d => d.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var analysis = await openAi.SearchAsync(query, adverts, discussions, ct);

        var advertById = adverts.ToDictionary(a => a.Id);
        var matchedAdverts = analysis.AdvertIds
            .Where(advertById.ContainsKey)
            .Select(id => advertById[id])
            .OrderByDescending(a => a.ViewCount)
            .ThenByDescending(a => a.CreatedAt)
            .ToList();

        var savedIds = await AdvertService.LoadSavedAdvertIdsAsync(
            db,
            viewerUserId,
            matchedAdverts.Select(a => a.Id),
            ct);
        var advertResults = AdvertService.ToDtos(matchedAdverts, savedIds);

        var discussionById = discussions.ToDictionary(d => d.Id);
        var discussionResults = analysis.DiscussionIds
            .Where(discussionById.ContainsKey)
            .Select(id => discussionById[id])
            .OrderByDescending(d => d.ReplyCount)
            .ThenByDescending(d => d.CreatedAt)
            .Select(ToDiscussionDto)
            .ToList();

        if (request.Tab.Equals("annonces", StringComparison.OrdinalIgnoreCase))
            discussionResults = [];
        else if (request.Tab.Equals("discussions", StringComparison.OrdinalIgnoreCase))
            advertResults = [];

        var totalAdverts = advertResults.Count;
        var totalDiscussions = discussionResults.Count;
        var skip = (page - 1) * pageSize;

        var pagedAdverts = advertResults.Skip(skip).Take(pageSize).ToList();
        var pagedDiscussions = discussionResults.Skip(skip).Take(pageSize).ToList();

        return new SearchResultDto(
            pagedAdverts,
            pagedDiscussions,
            analysis.Summary,
            new SearchPaginationDto(
                page,
                pageSize,
                totalAdverts,
                totalDiscussions,
                skip + pagedAdverts.Count < totalAdverts,
                skip + pagedDiscussions.Count < totalDiscussions));
    }

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));

    private async Task RecordSearchQueryAsync(string query, CancellationToken ct = default)
    {
        var normalized = SearchQueryHelper.Normalize(query);
        if (normalized is null)
            return;

        var display = SearchQueryHelper.Display(query);
        var existing = await db.SearchQueryStats
            .FirstOrDefaultAsync(s => s.NormalizedQuery == normalized, ct);

        if (existing is null)
        {
            db.SearchQueryStats.Add(new SearchQueryStat
            {
                NormalizedQuery = normalized,
                DisplayQuery = display,
            });
        }
        else
        {
            existing.SearchCount++;
            existing.DisplayQuery = display;
            existing.LastSearchedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        cache.Remove(ApiCacheKeys.PopularSearches);
    }

    public async Task<PagedResultDto<PopularSearchDto>> GetPopularSearchesAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var cacheKey = $"{ApiCacheKeys.PopularSearches}:{normalizedPage}:{normalizedPageSize}";

        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = PopularSearchesCacheDuration;

            var query = db.SearchQueryStats
                .AsNoTracking()
                .OrderByDescending(s => s.SearchCount)
                .ThenByDescending(s => s.LastSearchedAt);

            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .Select(s => new PopularSearchDto(s.DisplayQuery, s.SearchCount))
                .ToListAsync(ct);

            return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
        }) ?? PagingHelper.Create(Array.Empty<PopularSearchDto>(), normalizedPage, normalizedPageSize, 0);
    }

    public async Task<CategorizeResponseDto> CategorizeAsync(string text, CancellationToken ct = default)
    {
        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var beforeCount = categories.Count;
        var analysis = await openAi.AnalyzeAdvertAsync(text, categories, ct);

        var categoryCreated = false;
        if (analysis.CreateNewCategory && categories.All(c => c.Slug != analysis.CategorySlug))
        {
            var created = await CategoryResolver.ResolveOrCreateCategoryAsync(db, analysis, cache, ct);
            analysis = analysis with { CategorySlug = created.Slug, CategoryLabel = created.Label, CategoryIcon = created.Icon };
            categoryCreated = true;
        }

        var intentLabel = analysis.Intent switch
        {
            "offre" => "Offre — le client vend ou propose",
            "discussion" => "Discussion communautaire",
            _ => "Demande — le client cherche",
        };

        return new CategorizeResponseDto(
            analysis.CategorySlug,
            analysis.CategoryLabel,
            analysis.CategoryIcon,
            analysis.Intent,
            intentLabel,
            analysis.Confidence,
            analysis.Summary,
            analysis.RuleBasedFallback ? "rules" : "openai",
            categoryCreated
        );
    }

    private static DiscussionDto ToDiscussionDto(Discussion d) =>
        new(
            d.Id,
            d.Title,
            d.Body,
            d.User.DisplayName,
            TimeHelpers.Initials(d.User.DisplayName),
            d.ReplyCount,
            TimeHelpers.FormatRelative(d.CreatedAt),
            d.Category?.Slug
        );
}
