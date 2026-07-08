using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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
public class SearchService(
    KinshoutDbContext db,
    IOpenAiService openAi,
    IMemoryCache cache,
    IAdvertDtoMapper advertDtos,
    IServiceScopeFactory? scopeFactory = null) : ISearchService
{

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private static readonly TimeSpan PopularSearchesCacheDuration = TimeSpan.FromSeconds(30);
    public async Task<SearchResultDto> SearchAsync(SearchRequestDto request, Guid? viewerUserId = null, CancellationToken ct = default)
    {

        var query = request.Query.Trim();
        var (page, pageSize) = NormalizePaging(request.Page, request.PageSize);
        var isSemanticSearch = !string.IsNullOrWhiteSpace(query);
        var isAdvertBrowse = IsExplicitAdvertBrowse(request);
        var isTopicBrowse = IsExplicitTopicBrowse(request);
        if (page == 1 && isSemanticSearch)
        {
            if (scopeFactory is null)
                await RecordSearchQueryAsync(query, ct);
            else
                QueueRecordSearchQuery(query);
        }

        var hints = SearchQueryHints(isSemanticSearch, query);
        var browseCategory = isAdvertBrowse
            ? await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CategoryId, ct)
            : null;
        var browseTopic = isTopicBrowse
            ? await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.TopicId, ct)
            : null;

        List<Advert> adverts;
        List<Discussion> discussions;
        if (scopeFactory is not null)
        {
            var advertsTask = LoadAdvertsInScopeAsync(request, hints, query, isAdvertBrowse, isSemanticSearch, ct);
            var discussionsTask = LoadDiscussionsInScopeAsync(request, hints, query, isTopicBrowse, isSemanticSearch, ct);
            await Task.WhenAll(advertsTask, discussionsTask);
            adverts = await advertsTask;
            discussions = await discussionsTask;
        }
        else
        {
            adverts = await LoadAdvertsAsync(request, hints, query, isAdvertBrowse, isSemanticSearch, ct);
            discussions = await LoadDiscussionsAsync(request, hints, query, isTopicBrowse, isSemanticSearch, ct);
        }
        var analysis = await AnalyzeAsync(
            query,
            hints,
            isAdvertBrowse,
            isTopicBrowse,
            isSemanticSearch,
            browseCategory,
            browseTopic,
            adverts,
            discussions,
            ct);
        var sort = ListSortHelper.IsPopular(request.Sort) ? ListSortHelper.Popular : ListSortHelper.Recent;
        var advertById = adverts.ToDictionary(a => a.Id);
        var matchedAdverts = SortAdverts(
            analysis.AdvertIds
                .Where(advertById.ContainsKey)
                .Select(id => advertById[id]),
            sort);
        var discussionById = discussions.ToDictionary(d => d.Id);
        var matchedDiscussions = SortDiscussions(
            analysis.DiscussionIds
                .Where(discussionById.ContainsKey)
                .Select(id => discussionById[id]),
            sort);
        var savedIds = await AdvertService.LoadSavedAdvertIdsAsync(
            db,
            viewerUserId,
            matchedAdverts.Select(a => a.Id),
            ct);
        var likedDiscussionIds = await DiscussionService.LoadLikedDiscussionIdsAsync(
            db,
            viewerUserId,
            matchedDiscussions.Select(d => d.Id),
            ct);
        var advertResults = advertDtos.ToDtos(matchedAdverts, savedIds);
        var discussionResults = matchedDiscussions
            .Select(d => ToDiscussionDto(d, likedDiscussionIds.Contains(d.Id)))
            .ToList();
        if (request.Tab.Equals("all", StringComparison.OrdinalIgnoreCase))
            return BuildMixedSearchResult(
                advertResults,
                discussionResults,
                matchedAdverts,
                matchedDiscussions,
                analysis.Summary,
                sort,
                page,
                pageSize);
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
    private static bool IsExplicitAdvertBrowse(SearchRequestDto request) =>
        request.CategoryId is not null && string.IsNullOrWhiteSpace(request.Query);

    private static bool IsExplicitTopicBrowse(SearchRequestDto request) =>
        request.TopicId is not null && string.IsNullOrWhiteSpace(request.Query);

    private static SearchQueryHints SearchQueryHints(bool isSemanticSearch, string query) =>
        isSemanticSearch ? SearchQueryResolver.ParseHints(query) : new SearchQueryHints();

    private async Task<List<Advert>> LoadAdvertsInScopeAsync(
        SearchRequestDto request,
        SearchQueryHints hints,
        string query,
        bool isAdvertBrowse,
        bool isSemanticSearch,
        CancellationToken ct)
    {
        using var scope = scopeFactory!.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
        var scopedCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        return await LoadAdvertsCoreAsync(scopedDb, scopedCache, request, hints, query, isAdvertBrowse, isSemanticSearch, ct);
    }

    private async Task<List<Discussion>> LoadDiscussionsInScopeAsync(
        SearchRequestDto request,
        SearchQueryHints hints,
        string query,
        bool isTopicBrowse,
        bool isSemanticSearch,
        CancellationToken ct)
    {
        using var scope = scopeFactory!.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
        var scopedCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        return await LoadDiscussionsCoreAsync(scopedDb, scopedCache, request, hints, query, isTopicBrowse, isSemanticSearch, ct);
    }

    private async Task<List<Advert>> LoadAdvertsAsync(
        SearchRequestDto request,
        SearchQueryHints hints,
        string query,
        bool isAdvertBrowse,
        bool isSemanticSearch,
        CancellationToken ct) =>
        await LoadAdvertsCoreAsync(db, cache, request, hints, query, isAdvertBrowse, isSemanticSearch, ct);

    private async Task<List<Advert>> LoadAdvertsCoreAsync(
        KinshoutDbContext context,
        IMemoryCache memoryCache,
        SearchRequestDto request,
        SearchQueryHints hints,
        string query,
        bool isAdvertBrowse,
        bool isSemanticSearch,
        CancellationToken ct)
    {

        if (request.Tab.Equals("discussions", StringComparison.OrdinalIgnoreCase))
            return [];
        if (isAdvertBrowse)
            return await LoadAdvertsByCategoryIdAsync(context, request.CategoryId!.Value, request, ct);
        if (!isSemanticSearch)
            return [];
        return await LoadSemanticAdvertsAsync(context, memoryCache, query, hints, request, ct);

    }

    private async Task<List<Discussion>> LoadDiscussionsAsync(
        SearchRequestDto request,
        SearchQueryHints hints,
        string query,
        bool isTopicBrowse,
        bool isSemanticSearch,
        CancellationToken ct) =>
        await LoadDiscussionsCoreAsync(db, cache, request, hints, query, isTopicBrowse, isSemanticSearch, ct);

    private async Task<List<Discussion>> LoadDiscussionsCoreAsync(
        KinshoutDbContext context,
        IMemoryCache memoryCache,
        SearchRequestDto request,
        SearchQueryHints hints,
        string query,
        bool isTopicBrowse,
        bool isSemanticSearch,
        CancellationToken ct)
    {

        if (request.Tab.Equals("annonces", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!SearchDiscussionScope.ShouldSearchDiscussions(request, hints, query))
            return [];
        if (isTopicBrowse)
            return await LoadDiscussionsByTopicIdAsync(context, request.TopicId!.Value, ct);
        if (!isSemanticSearch)
            return [];
        return await LoadSemanticDiscussionsAsync(context, memoryCache, query, hints, ct);

    }

    private async Task<AiSearchAnalysis> AnalyzeAsync(
        string query,
        SearchQueryHints hints,
        bool isAdvertBrowse,
        bool isTopicBrowse,
        bool isSemanticSearch,
        Category? browseCategory,
        Category? browseTopic,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions,
        CancellationToken ct)
    {

        if (!isSemanticSearch)
        {
            return new AiSearchAnalysis(
                adverts.Select(a => a.Id).ToList(),
                discussions.Select(d => d.Id).ToList(),
                BuildBrowseSummary(browseCategory, browseTopic, adverts.Count, discussions.Count));
        }

        var local = SearchMatchHelper.Rank(query, adverts, discussions);
        if (ShouldUseLocalRankOnly(query, hints, adverts, discussions, local))
            return local;
        if (adverts.Count == 0 && local.DiscussionIds.Count > 0)
            return local;
        if (discussions.Count == 0 && local.AdvertIds.Count > 0)
            return local;
        if (adverts.Count == 0 && discussions.Count > 0 && local.DiscussionIds.Count == 0)
            return local;

        var advertById = adverts.ToDictionary(a => a.Id);
        var discussionById = discussions.ToDictionary(d => d.Id);
        var advertCandidates = (local.AdvertIds.Count > 0
                ? local.AdvertIds.Where(advertById.ContainsKey).Select(id => advertById[id])
                : adverts)
            .Take(SearchRetrieval.OpenAiCandidateLimit)
            .ToList();
        var discussionCandidates = (local.DiscussionIds.Count > 0
                ? local.DiscussionIds.Where(discussionById.ContainsKey).Select(id => discussionById[id])
                : discussions)
            .Take(SearchRetrieval.OpenAiCandidateLimit)
            .ToList();
        if (advertCandidates.Count == 0 && discussionCandidates.Count == 0)
            return local;

        var ai = await openAi.SearchAsync(query, advertCandidates, discussionCandidates, ct);
        if (ai.AdvertIds.Count == 0 && ai.DiscussionIds.Count == 0)
            return local;

        return local.AdvertIds.Count == 0 && local.DiscussionIds.Count == 0
            ? ai
            : MergeLocalAndAiResults(local, ai);

    }

    private static string BuildBrowseSummary(Category? browseCategory, Category? browseTopic, int advertCount, int discussionCount)
    {
        if (browseTopic is not null)
        {
            if (discussionCount > 0)
                return $"{discussionCount} discussion{(discussionCount == 1 ? "" : "s")} — {browseTopic.Label}.";
            return "Aucun résultat.";
        }

        if (browseCategory is not null)
        {
            if (advertCount > 0)
                return $"{advertCount} annonce{(advertCount == 1 ? "" : "s")} — {browseCategory.Label}.";
            return "Aucun résultat.";
        }

        if (advertCount > 0 && discussionCount > 0)
            return $"{advertCount + discussionCount} résultats.";
        if (advertCount > 0)
            return $"{advertCount} annonce{(advertCount == 1 ? "" : "s")}.";
        if (discussionCount > 0)
            return $"{discussionCount} discussion{(discussionCount == 1 ? "" : "s")}.";
        return "Aucun résultat.";
    }
    private static AiSearchAnalysis MergeLocalAndAiResults(AiSearchAnalysis local, AiSearchAnalysis ai)
    {

        var advertIds = ai.AdvertIds
            .Concat(local.AdvertIds.Where(id => !ai.AdvertIds.Contains(id)))
            .ToList();
        var discussionIds = ai.DiscussionIds
            .Concat(local.DiscussionIds.Where(id => !ai.DiscussionIds.Contains(id)))
            .ToList();
        return new AiSearchAnalysis(
            advertIds,
            discussionIds,
            string.IsNullOrWhiteSpace(ai.Summary) ? local.Summary : ai.Summary);

    }
    private async Task<List<Advert>> LoadAdvertsByCategoryIdAsync(
        KinshoutDbContext context,
        Guid categoryId,
        SearchRequestDto request,
        CancellationToken ct)
    {
        IQueryable<Advert> query = context.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.IsPublished && a.CategoryId == categoryId);
        query = ApplyRequestAdvertFilters(query, request);
        return await query.ToListAsync(ct);
    }

    private static async Task<List<Advert>> LoadSemanticAdvertsAsync(
        KinshoutDbContext context,
        IMemoryCache memoryCache,
        string query,
        SearchQueryHints hints,
        SearchRequestDto request,
        CancellationToken ct)
    {
        var strict = await LoadSemanticAdvertsWithHintsAsync(context, memoryCache, query, hints, request, ct);
        if (strict.Count > 0 || !HasStructuredHints(hints))
            return strict;

        return await LoadSemanticAdvertsWithHintsAsync(context, memoryCache, query, new SearchQueryHints(), request, ct);
    }

    private static async Task<List<Advert>> LoadSemanticAdvertsWithHintsAsync(
        KinshoutDbContext context,
        IMemoryCache memoryCache,
        string query,
        SearchQueryHints hints,
        SearchRequestDto request,
        CancellationToken ct)
    {
        IQueryable<Advert> advertQuery = context.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.IsPublished);
        advertQuery = ApplyRequestAdvertFilters(advertQuery, request);
        if (!string.IsNullOrWhiteSpace(hints.ParentCategorySlug))
        {
            var categorySlugs = SearchQueryResolver.CategorySlugsForParent(hints.ParentCategorySlug);
            advertQuery = advertQuery.Where(a => a.Category != null && categorySlugs.Contains(a.Category.Slug));
        }
        if (!string.IsNullOrWhiteSpace(hints.SubcategorySlug))
            advertQuery = advertQuery.Where(a => a.SubcategorySlug == hints.SubcategorySlug);
        foreach (var location in hints.LocationTerms)
            advertQuery = SearchDbTextFilter.WhereAdvertLocationContains(advertQuery, context, location);

        return await SearchRetrieval.LoadSemanticAdvertsAsync(context, advertQuery, query, memoryCache, ct);
    }

    private static bool HasStructuredHints(SearchQueryHints hints) =>
        hints.LocationTerms.Count > 0
        || !string.IsNullOrWhiteSpace(hints.SubcategorySlug)
        || !string.IsNullOrWhiteSpace(hints.ParentCategorySlug);

    private static async Task<List<Discussion>> LoadDiscussionsByTopicIdAsync(
        KinshoutDbContext context,
        Guid topicId,
        CancellationToken ct) =>
        await OrderDiscussions(
            context.Discussions
                .AsNoTracking()
                .Include(d => d.User)
                .Include(d => d.Category)
                .Where(d => d.CategoryId == topicId))
            .ToListAsync(ct);

    private static async Task<List<Discussion>> LoadSemanticDiscussionsAsync(
        KinshoutDbContext context,
        IMemoryCache memoryCache,
        string query,
        SearchQueryHints hints,
        CancellationToken ct)
    {
        var strict = await LoadSemanticDiscussionsWithHintsAsync(context, memoryCache, query, hints, ct);
        if (strict.Count > 0 || !HasStructuredHints(hints))
            return strict;

        return await LoadSemanticDiscussionsWithHintsAsync(context, memoryCache, query, new SearchQueryHints(), ct);
    }

    private static async Task<List<Discussion>> LoadSemanticDiscussionsWithHintsAsync(
        KinshoutDbContext context,
        IMemoryCache memoryCache,
        string query,
        SearchQueryHints hints,
        CancellationToken ct)
    {
        IQueryable<Discussion> discussionQuery = context.Discussions.AsNoTracking();
        foreach (var location in hints.LocationTerms)
            discussionQuery = SearchDbTextFilter.WhereTitleOrBodyContains(discussionQuery, context, location);

        return await SearchRetrieval.LoadSemanticDiscussionsAsync(context, discussionQuery, query, memoryCache, ct);
    }

    private static IQueryable<Advert> ApplyRequestAdvertFilters(IQueryable<Advert> query, SearchRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.Intent))
        {
            var intent = request.Intent.Trim().ToLowerInvariant();
            if (intent == SearchIntentHelper.Offre)
                query = query.Where(a => a.Intent == AdvertIntent.Offre);
            else if (intent == SearchIntentHelper.Demande)
                query = query.Where(a => a.Intent == AdvertIntent.Demande);
            else if (intent == SearchIntentHelper.Discussion)
                query = query.Where(a => a.Intent == AdvertIntent.Discussion);
        }

        return AdvertSourceMapper.ApplySourceFilter(
            query,
            AdvertSourceMapper.NormalizeListFilter(request.Source));
    }

    private static IQueryable<Discussion> OrderDiscussions(IQueryable<Discussion> query) =>
        query.OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt);

    private static SearchResultDto BuildMixedSearchResult(
        IReadOnlyList<AdvertDto> advertResults,
        IReadOnlyList<DiscussionDto> discussionResults,
        IReadOnlyList<Advert> matchedAdverts,
        IReadOnlyList<Discussion> matchedDiscussions,
        string? summary,
        string sort,
        int page,
        int pageSize)
    {

        var advertMeta = matchedAdverts.ToDictionary(a => a.Id);
        var discussionMeta = matchedDiscussions.ToDictionary(d => d.Id);
        var feed = new List<(string Kind, AdvertDto? Advert, DiscussionDto? Discussion, DateTime CreatedAt, int ViewCount, int SourceEngagement)>();
        foreach (var advert in advertResults)
        {

            var meta = advertMeta[advert.Id];
            feed.Add(("advert", advert, null, AdvertSourceMapper.SortDate(meta), meta.ViewCount, 0));

        }
        foreach (var discussion in discussionResults)
        {

            var meta = discussionMeta[discussion.Id];
            feed.Add(("discussion", null, discussion, meta.CreatedAt, meta.ViewCount, meta.SourceEngagementScore ?? 0));

        }
        feed = sort == ListSortHelper.Popular
            ? feed.OrderByDescending(x => x.ViewCount)
                .ThenByDescending(x => x.SourceEngagement)
                .ThenByDescending(x => x.CreatedAt)
                .ToList()
            : feed.OrderByDescending(x => x.CreatedAt).ToList();
        var totalItems = feed.Count;
        var skip = (page - 1) * pageSize;
        var pageItems = feed.Skip(skip).Take(pageSize).ToList();
        var items = pageItems
            .Select(x => new SearchFeedItemDto(x.Kind, x.Advert, x.Discussion))
            .ToList();
        return new SearchResultDto(
            [],
            [],
            summary,
            new SearchPaginationDto(
                page,
                pageSize,
                advertResults.Count,
                discussionResults.Count,
                false,
                false,
                totalItems,
                skip + pageItems.Count < totalItems),
            items);

    }
    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));

    private static bool ShouldUseLocalRankOnly(
        string query,
        SearchQueryHints hints,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions,
        AiSearchAnalysis local)
    {
        if (SearchMatchHelper.IsConfidentLocalRank(query, adverts, discussions))
            return true;

        if (!string.IsNullOrWhiteSpace(hints.SubcategorySlug)
            && local.AdvertIds.Count > 0
            && discussions.Count == 0)
        {
            return true;
        }

        return false;
    }

    private void QueueRecordSearchQuery(string query)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory!.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
                var scopedCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
                await RecordSearchQueryAsync(scopedDb, scopedCache, query, CancellationToken.None);
            }
            catch
            {
                // Best-effort analytics only.
            }
        });
    }

    private static List<Advert> SortAdverts(IEnumerable<Advert> adverts, string sort)
    {

        var query = adverts.AsEnumerable();
        return sort == ListSortHelper.Popular
            ? query.OrderByDescending(a => a.ViewCount).ThenByDescending(AdvertSourceMapper.SortDate).ToList()
            : query.OrderByDescending(AdvertSourceMapper.SortDate).ToList();

    }

    private static List<Discussion> SortDiscussions(IEnumerable<Discussion> discussions, string sort)
    {

        var query = discussions.AsEnumerable();
        return sort == ListSortHelper.Popular
            ? DiscussionSourceMapper.OrderByPopular(query).ToList()
            : query.OrderByDescending(d => d.CreatedAt).ToList();

    }
    private Task RecordSearchQueryAsync(string query, CancellationToken ct = default) =>
        RecordSearchQueryAsync(db, cache, query, ct);

    private static async Task RecordSearchQueryAsync(
        KinshoutDbContext db,
        IMemoryCache cache,
        string query,
        CancellationToken ct)
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
            var rows = await db.SearchQueryStats.AsNoTracking().ToListAsync(ct);
            var grouped = rows
                .GroupBy(SearchQueryHelper.ResolveStatKey, StringComparer.Ordinal)
                .Select(g =>
                {
                    var keeper = g
                        .OrderByDescending(s => s.LastSearchedAt)
                        .ThenByDescending(s => s.SearchCount)
                        .First();
                    return new
                    {
                        keeper.DisplayQuery,
                        Count = g.Sum(s => s.SearchCount),
                        LastSearchedAt = g.Max(s => s.LastSearchedAt),
                    };
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.LastSearchedAt)
                .ToList();

            var total = grouped.Count;
            var items = grouped
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .Select(x => new PopularSearchDto(x.DisplayQuery, x.Count))
                .ToList();
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
    private static DiscussionDto ToDiscussionDto(Discussion d, bool isLiked = false) =>
        DiscussionService.ToListDto(d, isLiked);

}

