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
public class SearchService(KinshoutDbContext db, IOpenAiService openAi, IMemoryCache cache, IAdvertDtoMapper advertDtos) : ISearchService
{

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int OpenAiCandidateLimit = 400;
    private static readonly TimeSpan PopularSearchesCacheDuration = TimeSpan.FromSeconds(30);
    public async Task<SearchResultDto> SearchAsync(SearchRequestDto request, Guid? viewerUserId = null, CancellationToken ct = default)
    {

        var query = request.Query.Trim();
        var (page, pageSize) = NormalizePaging(request.Page, request.PageSize);
        if (page == 1)
            await RecordSearchQueryAsync(query, ct);
        var allCategories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var advertCategories = allCategories
            .Where(c => !c.IsDiscussionTopic && c.Slug != Category.DiscussionSlug)
            .ToList();
        var topicCategories = allCategories.Where(c => c.IsDiscussionTopic).ToList();
        var parsed = SearchQueryResolver.Parse(query, advertCategories, topicCategories);
        var adverts = await LoadAdvertsAsync(parsed, ct);
        var discussions = await LoadDiscussionsAsync(parsed, ct);
        var analysis = await AnalyzeAsync(query, parsed, adverts, discussions, ct);
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
        ApplyIntentFilter(request.Intent, ref matchedAdverts, ref matchedDiscussions);
        ApplySourceFilter(request.Source, ref matchedAdverts);
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
    private async Task<List<Advert>> LoadAdvertsAsync(ParsedSearchQuery parsed, CancellationToken ct)
    {

        if (parsed.IsAdvertCategoryBrowse || parsed.IsStructuredAdvertSearch)
            return await LoadFilteredAdvertsAsync(parsed, ct);
        return await LoadAllPublishedAdvertsAsync(ct);

    }
    private async Task<List<Discussion>> LoadDiscussionsAsync(ParsedSearchQuery parsed, CancellationToken ct)
    {

        if (parsed.DiscussionTopic is not null)
            return await LoadFilteredDiscussionsAsync(parsed, ct);
        if (parsed.IsStructuredAdvertSearch || parsed.IsAdvertCategoryBrowse)
            return [];
        return await LoadAllPublishedDiscussionsAsync(ct);

    }
    private async Task<AiSearchAnalysis> AnalyzeAsync(
        string query,
        ParsedSearchQuery parsed,
        IReadOnlyList<Advert> adverts,
        IReadOnlyList<Discussion> discussions,
        CancellationToken ct)
    {

        if (parsed.IsAdvertCategoryBrowse || parsed.IsStructuredAdvertSearch)
        {

            return new AiSearchAnalysis(
                adverts.Select(a => a.Id).ToList(),
                parsed.DiscussionTopic is not null ? discussions.Select(d => d.Id).ToList() : [],
                BuildAdvertSummary(parsed, adverts.Count));

        }
        if (parsed.IsDiscussionTopicBrowse || parsed.IsDiscussionTopicSearch)
        {

            return new AiSearchAnalysis(
                parsed.AdvertCategory is not null ? adverts.Select(a => a.Id).ToList() : [],
                discussions.Select(d => d.Id).ToList(),
                BuildDiscussionSummary(parsed, discussions.Count));

        }
        var local = SearchMatchHelper.Rank(query, adverts, discussions);
        var advertById = adverts.ToDictionary(a => a.Id);
        var discussionById = discussions.ToDictionary(d => d.Id);
        var advertCandidates = (local.AdvertIds.Count > 0
                ? local.AdvertIds.Where(advertById.ContainsKey).Select(id => advertById[id])
                : adverts)
            .Take(OpenAiCandidateLimit)
            .ToList();
        var discussionCandidates = (local.DiscussionIds.Count > 0
                ? local.DiscussionIds.Where(discussionById.ContainsKey).Select(id => discussionById[id])
                : discussions)
            .Take(OpenAiCandidateLimit)
            .ToList();
        if (advertCandidates.Count == 0 && discussionCandidates.Count == 0)
            return local;
        var ai = await openAi.SearchAsync(query, advertCandidates, discussionCandidates, ct);
        return local.AdvertIds.Count == 0 && local.DiscussionIds.Count == 0
            ? ai
            : MergeLocalAndAiResults(local, ai);

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
    private async Task<List<Advert>> LoadFilteredAdvertsAsync(ParsedSearchQuery parsed, CancellationToken ct)
    {

        if (parsed.AdvertCategory is null || parsed.AdvertCategory.Id == Guid.Empty)
            return [];
        IQueryable<Advert> query = db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.IsPublished && a.CategoryId == parsed.AdvertCategory.Id);
        if (!string.IsNullOrWhiteSpace(parsed.SubcategorySlug))
            query = query.Where(a => a.SubcategorySlug == parsed.SubcategorySlug);
        foreach (var location in parsed.LocationTerms)
        {

            var term = location;
            query = query.Where(a => a.Location != null && a.Location.Contains(term));

        }
        return await query.ToListAsync(ct);

    }
    private async Task<List<Discussion>> LoadFilteredDiscussionsAsync(ParsedSearchQuery parsed, CancellationToken ct)
    {

        IQueryable<Discussion> query = db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category);
        if (parsed.DiscussionTopic is not null && parsed.DiscussionTopic.Id != Guid.Empty)
            query = query.Where(d => d.CategoryId == parsed.DiscussionTopic.Id);
        foreach (var location in parsed.LocationTerms)
        {

            var term = location;
            query = query.Where(d => d.Title.Contains(term) || d.Body.Contains(term));

        }
        return await query
            .OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

    }
    private async Task<List<Advert>> LoadAllPublishedAdvertsAsync(CancellationToken ct) =>
        await db.Adverts
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.User)
            .Where(a => a.IsPublished)
            .ToListAsync(ct);
    private async Task<List<Discussion>> LoadAllPublishedDiscussionsAsync(CancellationToken ct) =>
        await db.Discussions
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Category)
            .OrderByDescending(d => d.ViewCount)
            .ThenByDescending(d => d.SourceEngagementScore ?? 0)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    private static string BuildAdvertSummary(ParsedSearchQuery parsed, int count)
    {

        var parts = new List<string>();
        if (parsed.AdvertCategory is not null)
            parts.Add(parsed.AdvertCategory.Label);
        if (!string.IsNullOrWhiteSpace(parsed.SubcategorySlug))
            parts.Add(parsed.SubcategorySlug.Replace('_', ' '));
        if (parsed.LocationTerms.Count > 0)
            parts.Add(string.Join(", ", parsed.LocationTerms));
        var scope = parts.Count > 0 ? string.Join(" · ", parts) : "annonces";
        return $"{count} annonce{(count == 1 ? "" : "s")} — {scope}.";

    }
    private static string BuildDiscussionSummary(ParsedSearchQuery parsed, int count)
    {

        var topic = parsed.DiscussionTopic?.Label ?? "discussions";
        if (parsed.LocationTerms.Count > 0)
            return $"{count} discussion{(count == 1 ? "" : "s")} — {topic} · {string.Join(", ", parsed.LocationTerms)}.";
        return $"{count} discussion{(count == 1 ? "" : "s")} — {topic}.";

    }
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
    private static List<Advert> SortAdverts(IEnumerable<Advert> adverts, string sort)
    {

        var query = adverts.AsEnumerable();
        return sort == ListSortHelper.Popular
            ? query.OrderByDescending(a => a.ViewCount).ThenByDescending(AdvertSourceMapper.SortDate).ToList()
            : query.OrderByDescending(AdvertSourceMapper.SortDate).ToList();

    }
    private static void ApplySourceFilter(string? source, ref List<Advert> adverts)
    {

        var sourceFilter = AdvertSourceMapper.NormalizeListFilter(source);
        if (string.IsNullOrWhiteSpace(sourceFilter))
            return;
        if (sourceFilter == "external")
        {

            adverts = adverts.Where(a => a.IsExternal).ToList();
            return;

        }
        if (sourceFilter == AdvertSourceProvider.Kinshout)
        {

            adverts = adverts.Where(a => !a.IsExternal).ToList();
            return;

        }
        adverts = adverts.Where(a => a.SourceProvider == sourceFilter).ToList();

    }
    private static List<Discussion> SortDiscussions(IEnumerable<Discussion> discussions, string sort)
    {

        var query = discussions.AsEnumerable();
        return sort == ListSortHelper.Popular
            ? DiscussionSourceMapper.OrderByPopular(query).ToList()
            : query.OrderByDescending(d => d.CreatedAt).ToList();

    }
    private static void ApplyIntentFilter(
        string? intent,
        ref List<Advert> adverts,
        ref List<Discussion> discussions)
    {

        if (string.IsNullOrWhiteSpace(intent))
            return;
        switch (intent.Trim().ToLowerInvariant())
        {

            case SearchIntentHelper.Offre:
                adverts = adverts.Where(a => a.Intent == AdvertIntent.Offre).ToList();
                discussions = [];
                break;
            case SearchIntentHelper.Demande:
                adverts = adverts.Where(a => a.Intent == AdvertIntent.Demande).ToList();
                discussions = [];
                break;
            case SearchIntentHelper.Discussion:
                adverts = adverts.Where(a => a.Intent == AdvertIntent.Discussion).ToList();
                break;

        }

    }
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
    private static DiscussionDto ToDiscussionDto(Discussion d, bool isLiked = false) =>
        DiscussionService.ToListDto(d, isLiked);

}

