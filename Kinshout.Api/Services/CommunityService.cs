using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface ICommunityService
{
    Task<PagedResultDto<CommunityDto>> ListAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        Guid? viewerUserId = null,
        CancellationToken ct = default);
    Task<SuggestCommunityResponseDto> SuggestAsync(string title, string body, CancellationToken ct = default);
    Task<CommunityDto?> GetBySlugAsync(
        string slugOrRoute,
        Guid? viewerUserId = null,
        CancellationToken ct = default);
    Task<CommunityDto> CreateAsync(Guid userId, CreateCommunityRequestDto request, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, string slugOrRoute, CancellationToken ct = default);
    Task RequestJoinAsync(Guid userId, string slugOrRoute, CancellationToken ct = default);
    Task InviteMemberAsync(Guid actorUserId, string slugOrRoute, Guid targetUserId, CancellationToken ct = default);
    Task ApproveMemberAsync(Guid actorUserId, string slugOrRoute, Guid targetUserId, CancellationToken ct = default);
    Task RejectMemberAsync(Guid actorUserId, string slugOrRoute, Guid targetUserId, CancellationToken ct = default);
    Task AddModeratorAsync(Guid actorUserId, string slugOrRoute, Guid targetUserId, CancellationToken ct = default);
    Task RemoveModeratorAsync(Guid actorUserId, string slugOrRoute, Guid targetUserId, CancellationToken ct = default);
    Task LeaveAsync(Guid userId, string slugOrRoute, CancellationToken ct = default);
    Task<PagedResultDto<CommunityMemberDto>> ListPendingMembersAsync(
        Guid actorUserId,
        string slugOrRoute,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
    Task EnsureCanAccessAsync(Guid communityId, Guid? viewerUserId, CancellationToken ct = default);
    Task EnsureCanPostAsync(Guid communityId, Guid userId, CancellationToken ct = default);
    /// <summary>
    /// Ensures the user has requested or holds membership. Idempotent when already approved or pending.
    /// </summary>
    Task EnsureJoinedAsync(Guid userId, Guid communityId, CancellationToken ct = default);
}

public class CommunityService(
    KinshoutDbContext db,
    IOpenAiService openAi,
    ICommunityJoinNotifier joinNotifier) : ICommunityService
{
    private const double MinSuggestionConfidence = 0.35;

    public async Task<PagedResultDto<CommunityDto>> ListAsync(
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        string sort = ListSortHelper.Recent,
        Guid? viewerUserId = null,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var query = db.Communities.AsNoTracking().AsQueryable();

        if (viewerUserId is null)
        {
            query = query.Where(c => c.Visibility == CommunityVisibilities.Public);
        }
        else
        {
            query = query.Where(c =>
                c.Visibility == CommunityVisibilities.Public
                || c.CreatedByUserId == viewerUserId
                || c.Members.Any(m =>
                    m.UserId == viewerUserId && m.Status == CommunityMemberStatuses.Approved));
        }

        query = ListSortHelper.IsPopular(sort)
            ? query.OrderByDescending(c => c.Discussions.Count).ThenByDescending(c => c.CreatedAt)
            : query.OrderByDescending(c => c.CreatedAt);

        var total = await query.CountAsync(ct);
        var communities = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var memberships = await LoadMembershipsAsync(
            communities.Select(c => c.Id).ToList(),
            viewerUserId,
            ct);

        var items = new List<CommunityDto>();
        foreach (var community in communities)
        {
            memberships.TryGetValue(community.Id, out var membership);
            var count = await db.Discussions.CountAsync(d => d.CommunityId == community.Id, ct);
            var modCount = await CountModeratorsAsync(community.Id, ct);
            items.Add(ToDto(community, count, modCount, membership, viewerUserId));
        }

        return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
    }

    public async Task<SuggestCommunityResponseDto> SuggestAsync(
        string title,
        string body,
        CancellationToken ct = default)
    {
        var trimmedTitle = title?.Trim() ?? "";
        var trimmedBody = body?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmedTitle) && string.IsNullOrWhiteSpace(trimmedBody))
            throw new ArgumentException("Le titre ou le texte est requis.");

        var text = string.IsNullOrWhiteSpace(trimmedTitle)
            ? trimmedBody
            : string.IsNullOrWhiteSpace(trimmedBody)
                ? trimmedTitle
                : $"{trimmedTitle}. {trimmedBody}";

        var communities = await db.Communities
            .AsNoTracking()
            .Where(c => c.Visibility == CommunityVisibilities.Public && c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        if (communities.Count == 0)
        {
            return new SuggestCommunityResponseDto(
                null,
                0,
                "Aucune communauté disponible pour le moment.",
                "none");
        }

        var analysis = await openAi.AnalyzeCommunityAsync(text, communities, ct);
        CommunityDto? match = null;
        if (!string.IsNullOrWhiteSpace(analysis.CommunitySlug) && analysis.Confidence >= MinSuggestionConfidence)
        {
            var community = communities.FirstOrDefault(c =>
                c.Slug.Equals(analysis.CommunitySlug, StringComparison.OrdinalIgnoreCase));
            if (community is not null)
            {
                var count = await db.Discussions.CountAsync(d => d.CommunityId == community.Id, ct);
                var modCount = await CountModeratorsAsync(community.Id, ct);
                match = ToDto(community, count, modCount);
            }
        }

        var source = match is null
            ? "none"
            : analysis.RuleBasedFallback
                ? "rules"
                : "openai";

        var summary = string.IsNullOrWhiteSpace(analysis.Summary)
            ? match is null
                ? "Aucune communauté ne correspond clairement à ce sujet."
                : $"Communauté suggérée : {match.RouteSlug}."
            : analysis.Summary;

        return new SuggestCommunityResponseDto(match, analysis.Confidence, summary, source);
    }

    public async Task<CommunityDto?> GetBySlugAsync(
        string slugOrRoute,
        Guid? viewerUserId = null,
        CancellationToken ct = default)
    {
        var slug = CommunitySlugHelper.Normalize(slugOrRoute);
        var community = await db.Communities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (community is null)
            return null;

        var membership = await FindMembershipAsync(community.Id, viewerUserId, ct);
        if (!CommunityAccessHelper.CanViewMetadata(community, membership, viewerUserId))
            return null;

        var count = await db.Discussions.CountAsync(d => d.CommunityId == community.Id, ct);
        var modCount = await CountModeratorsAsync(community.Id, ct);
        return ToDto(community, count, modCount, membership, viewerUserId);
    }

    public async Task<CommunityDto> CreateAsync(
        Guid userId,
        CreateCommunityRequestDto request,
        CancellationToken ct = default)
    {
        var slug = CommunitySlugHelper.Normalize(request.Slug);
        var name = string.IsNullOrWhiteSpace(request.Name) ? slug : request.Name.Trim();
        if (name.Length > 120)
            throw new ArgumentException("Le nom est trop long (max 120 caractères).");

        if (!CommunityVisibilityHelper.TryNormalize(request.Visibility, out var visibility))
            throw new ArgumentException("La visibilité doit être public ou private.");

        var description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();

        if (await db.Communities.AnyAsync(c => c.Slug == slug, ct))
            throw new ArgumentException($"La communauté k/{slug} existe déjà.");

        var community = new Community
        {
            Slug = slug,
            Name = name,
            Description = description,
            Visibility = visibility,
            CreatedByUserId = userId,
        };

        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = userId,
            Role = CommunityMemberRoles.Creator,
            Status = CommunityMemberStatuses.Approved,
            ReviewedAt = DateTime.UtcNow,
            ReviewedByUserId = userId,
        });

        await db.SaveChangesAsync(ct);
        var membership = await FindMembershipAsync(community.Id, userId, ct);
        return ToDto(community, discussionCount: 0, moderatorCount: 0, membership, userId);
    }

    public async Task DeleteAsync(Guid userId, string slugOrRoute, CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        if (community.CreatedByUserId != userId)
            throw new UnauthorizedAccessException("Seul le créateur peut supprimer cette communauté.");

        var hasDiscussions = await db.Discussions.AnyAsync(d => d.CommunityId == community.Id, ct);
        if (hasDiscussions)
            throw new InvalidOperationException(
                "Impossible de supprimer une communauté qui contient encore des discussions.");

        db.Communities.Remove(community);
        await db.SaveChangesAsync(ct);
    }

    public async Task RequestJoinAsync(Guid userId, string slugOrRoute, CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        var existing = await db.CommunityMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == userId, ct);

        if (existing?.Status == CommunityMemberStatuses.Approved)
            throw new InvalidOperationException("Vous êtes déjà membre de cette communauté.");

        await EnsureJoinedAsync(userId, community.Id, ct);
    }

    public async Task EnsureJoinedAsync(Guid userId, Guid communityId, CancellationToken ct = default)
    {
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new KeyNotFoundException("Communauté introuvable.");
        EnsureCommunityAcceptsMembershipChanges(community);

        if (community.CreatedByUserId == userId)
            return;

        var existing = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == userId, ct);

        if (existing?.Status == CommunityMemberStatuses.Approved)
            return;

        if (existing?.Status == CommunityMemberStatuses.Pending)
            return;

        if (existing?.Status == CommunityMemberStatuses.Rejected)
            db.CommunityMembers.Remove(existing);

        var isPrivate = CommunityVisibilityHelper.IsPrivate(community.Visibility);
        var status = isPrivate ? CommunityMemberStatuses.Pending : CommunityMemberStatuses.Approved;

        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = communityId,
            UserId = userId,
            Role = CommunityMemberRoles.Member,
            Status = status,
            ReviewedAt = isPrivate ? null : DateTime.UtcNow,
            ReviewedByUserId = isPrivate ? null : userId,
        });
        await db.SaveChangesAsync(ct);

        if (isPrivate)
        {
            var requester = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            await joinNotifier.NotifyJoinRequestAsync(community, requester, ct);
        }
    }

    public async Task InviteMemberAsync(
        Guid actorUserId,
        string slugOrRoute,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        EnsureCommunityAcceptsMembershipChanges(community);
        var actorMembership = await FindMembershipAsync(community.Id, actorUserId, ct);
        if (!CommunityAccessHelper.CanModerate(community, actorMembership, actorUserId))
            throw new UnauthorizedAccessException("Seuls le créateur ou un modérateur peuvent inviter des membres.");

        if (!await db.Users.AnyAsync(u => u.Id == targetUserId, ct))
            throw new KeyNotFoundException("Utilisateur introuvable.");

        var existing = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == targetUserId, ct);

        if (existing?.Status == CommunityMemberStatuses.Approved)
            throw new InvalidOperationException("Cet utilisateur est déjà membre de la communauté.");

        if (existing is not null)
            db.CommunityMembers.Remove(existing);

        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = community.Id,
            UserId = targetUserId,
            Role = CommunityMemberRoles.Member,
            Status = CommunityMemberStatuses.Approved,
            ReviewedAt = DateTime.UtcNow,
            ReviewedByUserId = actorUserId,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task ApproveMemberAsync(
        Guid actorUserId,
        string slugOrRoute,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        EnsureCommunityAcceptsMembershipChanges(community);
        var actorMembership = await FindMembershipAsync(community.Id, actorUserId, ct);
        if (!CommunityAccessHelper.CanModerate(community, actorMembership, actorUserId))
            throw new UnauthorizedAccessException("Seuls le créateur ou un modérateur peuvent approuver des membres.");

        var membership = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == targetUserId, ct)
            ?? throw new KeyNotFoundException("Demande d'adhésion introuvable.");

        if (membership.Status == CommunityMemberStatuses.Approved)
            return;

        membership.Status = CommunityMemberStatuses.Approved;
        membership.ReviewedAt = DateTime.UtcNow;
        membership.ReviewedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        var member = await db.Users.AsNoTracking().FirstAsync(u => u.Id == targetUserId, ct);
        await joinNotifier.NotifyJoinApprovedAsync(community, member, ct);
    }

    public async Task RejectMemberAsync(
        Guid actorUserId,
        string slugOrRoute,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        var actorMembership = await FindMembershipAsync(community.Id, actorUserId, ct);
        if (!CommunityAccessHelper.CanModerate(community, actorMembership, actorUserId))
            throw new UnauthorizedAccessException("Seuls le créateur ou un modérateur peuvent refuser des membres.");

        var membership = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == targetUserId, ct)
            ?? throw new KeyNotFoundException("Demande d'adhésion introuvable.");

        if (membership.Role == CommunityMemberRoles.Creator)
            throw new InvalidOperationException("Impossible de refuser le créateur.");

        membership.Status = CommunityMemberStatuses.Rejected;
        membership.ReviewedAt = DateTime.UtcNow;
        membership.ReviewedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        var member = await db.Users.AsNoTracking().FirstAsync(u => u.Id == targetUserId, ct);
        await joinNotifier.NotifyJoinRejectedAsync(community, member, ct);
    }

    public async Task AddModeratorAsync(
        Guid actorUserId,
        string slugOrRoute,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        EnsureCommunityAcceptsMembershipChanges(community);
        if (community.CreatedByUserId != actorUserId)
            throw new UnauthorizedAccessException("Seul le créateur peut nommer des modérateurs.");

        if (targetUserId == actorUserId)
            throw new InvalidOperationException("Le créateur est déjà modérateur principal.");

        var moderatorCount = await CountModeratorsAsync(community.Id, ct);
        if (moderatorCount >= CommunityMemberRoles.MaxModerators)
            throw new InvalidOperationException($"Maximum {CommunityMemberRoles.MaxModerators} modérateurs par communauté.");

        var membership = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == targetUserId, ct)
            ?? throw new KeyNotFoundException("Membre introuvable.");

        if (membership.Status != CommunityMemberStatuses.Approved)
            throw new InvalidOperationException("Seuls les membres approuvés peuvent devenir modérateurs.");

        if (membership.Role == CommunityMemberRoles.Creator)
            throw new InvalidOperationException("Le créateur ne peut pas être modérateur.");

        if (membership.Role == CommunityMemberRoles.Moderator)
            return;

        membership.Role = CommunityMemberRoles.Moderator;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveModeratorAsync(
        Guid actorUserId,
        string slugOrRoute,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        if (community.CreatedByUserId != actorUserId)
            throw new UnauthorizedAccessException("Seul le créateur peut retirer des modérateurs.");

        var membership = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == targetUserId, ct)
            ?? throw new KeyNotFoundException("Modérateur introuvable.");

        if (membership.Role != CommunityMemberRoles.Moderator)
            throw new InvalidOperationException("Cet utilisateur n'est pas modérateur.");

        membership.Role = CommunityMemberRoles.Member;
        await db.SaveChangesAsync(ct);
    }

    public async Task LeaveAsync(Guid userId, string slugOrRoute, CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        var membership = await db.CommunityMembers
            .FirstOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == userId, ct);

        if (community.CreatedByUserId == userId)
        {
            var successor = await db.CommunityMembers
                .Where(m =>
                    m.CommunityId == community.Id
                    && m.UserId != userId
                    && m.Status == CommunityMemberStatuses.Approved
                    && m.Role == CommunityMemberRoles.Moderator)
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (successor is not null)
            {
                community.CreatedByUserId = successor.UserId;
                successor.Role = CommunityMemberRoles.Creator;
            }
            else
            {
                community.IsActive = false;
            }

            if (membership is not null)
                db.CommunityMembers.Remove(membership);
        }
        else if (membership is not null)
        {
            db.CommunityMembers.Remove(membership);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResultDto<CommunityMemberDto>> ListPendingMembersAsync(
        Guid actorUserId,
        string slugOrRoute,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var community = await RequireCommunityAsync(slugOrRoute, ct);
        var actorMembership = await FindMembershipAsync(community.Id, actorUserId, ct);
        if (!CommunityAccessHelper.CanModerate(community, actorMembership, actorUserId))
            throw new UnauthorizedAccessException("Seuls le créateur ou un modérateur peuvent voir les demandes.");

        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var query = db.CommunityMembers
            .AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.CommunityId == community.Id && m.Status == CommunityMemberStatuses.Pending)
            .OrderBy(m => m.CreatedAt);

        var total = await query.CountAsync(ct);
        var rows = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(m => new CommunityMemberDto(
                m.UserId,
                m.User.DisplayName,
                m.Role,
                m.Status,
                m.CreatedAt))
            .ToList();

        return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
    }

    public async Task EnsureCanAccessAsync(Guid communityId, Guid? viewerUserId, CancellationToken ct = default)
    {
        var community = await db.Communities.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new KeyNotFoundException("Communauté introuvable.");

        var membership = await FindMembershipAsync(communityId, viewerUserId, ct);
        if (!CommunityAccessHelper.CanViewDiscussions(community, membership, viewerUserId))
            throw new UnauthorizedAccessException("Accès refusé. Rejoignez la communauté ou attendez l'approbation.");
    }

    public async Task EnsureCanPostAsync(Guid communityId, Guid userId, CancellationToken ct = default)
    {
        var community = await db.Communities.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new KeyNotFoundException("Communauté introuvable.");

        var membership = await FindMembershipAsync(communityId, userId, ct);
        if (!CommunityAccessHelper.CanPost(community, membership, userId))
        {
            if (!community.IsActive)
                throw new UnauthorizedAccessException("Cette communauté est inactive (lecture seule).");

            throw new UnauthorizedAccessException("Vous devez être membre approuvé pour publier dans cette communauté.");
        }
    }

    private static void EnsureCommunityAcceptsMembershipChanges(Community community)
    {
        if (!community.IsActive)
            throw new InvalidOperationException("Cette communauté est inactive (lecture seule).");
    }

    private async Task<int> CountModeratorsAsync(Guid communityId, CancellationToken ct) =>
        await db.CommunityMembers.CountAsync(
            m => m.CommunityId == communityId
                && m.Role == CommunityMemberRoles.Moderator
                && m.Status == CommunityMemberStatuses.Approved,
            ct);

    private async Task<Community> RequireCommunityAsync(string slugOrRoute, CancellationToken ct)
    {
        var slug = CommunitySlugHelper.Normalize(slugOrRoute);
        return await db.Communities.FirstOrDefaultAsync(c => c.Slug == slug, ct)
            ?? throw new KeyNotFoundException("Communauté introuvable.");
    }

    private async Task<CommunityMember?> FindMembershipAsync(
        Guid communityId,
        Guid? userId,
        CancellationToken ct)
    {
        if (userId is null)
            return null;

        return await db.CommunityMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == userId, ct);
    }

    private async Task<Dictionary<Guid, CommunityMember>> LoadMembershipsAsync(
        IReadOnlyList<Guid> communityIds,
        Guid? viewerUserId,
        CancellationToken ct)
    {
        if (viewerUserId is null || communityIds.Count == 0)
            return [];

        return await db.CommunityMembers
            .AsNoTracking()
            .Where(m => m.UserId == viewerUserId && communityIds.Contains(m.CommunityId))
            .ToDictionaryAsync(m => m.CommunityId, ct);
    }

    public static CommunityDto ToDto(
        Community community,
        int discussionCount,
        int moderatorCount,
        CommunityMember? membership = null,
        Guid? viewerUserId = null) =>
        new(
            community.Id,
            CommunitySlugHelper.ToRouteSlug(community.Slug),
            community.Slug,
            community.Name,
            community.Description,
            community.Visibility,
            community.IsActive,
            discussionCount,
            moderatorCount,
            community.CreatedByUserId,
            community.CreatedAt,
            membership?.Status,
            CommunityAccessHelper.CanViewDiscussions(community, membership, viewerUserId),
            viewerUserId is not null && CommunityAccessHelper.CanPost(community, membership, viewerUserId.Value),
            CommunityAccessHelper.CanModerate(community, membership, viewerUserId));
}
