using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public interface ICommunityJoinNotifier
{
    Task NotifyJoinRequestAsync(Community community, User requester, CancellationToken ct = default);
    Task NotifyJoinApprovedAsync(Community community, User member, CancellationToken ct = default);
    Task NotifyJoinRejectedAsync(Community community, User member, CancellationToken ct = default);
}

public class CommunityJoinNotifier(
    KinshoutDbContext db,
    IEmailService email,
    IOptions<EmailSettings> emailOptions,
    ILogger<CommunityJoinNotifier> logger) : ICommunityJoinNotifier
{
    public async Task NotifyJoinRequestAsync(Community community, User requester, CancellationToken ct = default)
    {
        var route = CommunitySlugHelper.ToRouteSlug(community.Slug);
        var recipients = await LoadModeratorRecipientsAsync(community, ct);
        var subject = $"Demande d'adhésion — {route}";
        var body =
            $"""
            Bonjour,

            {requester.DisplayName} ({requester.Email}) demande à rejoindre la communauté {route} (« {community.Name} »).

            Connectez-vous pour approuver ou refuser la demande (une seule approbation suffit) :
            {CommunityUrl(route, "/members/pending")}

            — Kinoiserie
            """;

        await SendToManyAsync(recipients, subject, body, ct);
    }

    public async Task NotifyJoinApprovedAsync(Community community, User member, CancellationToken ct = default)
    {
        var route = CommunitySlugHelper.ToRouteSlug(community.Slug);
        var subject = $"Accès accordé — {route}";
        var body =
            $"""
            Bonjour {member.DisplayName},

            Votre demande pour rejoindre {route} (« {community.Name} ») a été acceptée. Vous pouvez y participer dès maintenant :
            {CommunityUrl(route)}

            — Kinoiserie
            """;

        await email.SendAsync(member.Email, subject, body, ct);
    }

    public async Task NotifyJoinRejectedAsync(Community community, User member, CancellationToken ct = default)
    {
        var route = CommunitySlugHelper.ToRouteSlug(community.Slug);
        var subject = $"Demande refusée — {route}";
        var body =
            $"""
            Bonjour {member.DisplayName},

            Votre demande pour rejoindre {route} (« {community.Name} ») n'a pas été acceptée.

            — Kinoiserie
            """;

        await email.SendAsync(member.Email, subject, body, ct);
    }

    private async Task SendToManyAsync(
        IReadOnlyList<User> recipients,
        string subject,
        string body,
        CancellationToken ct)
    {
        foreach (var recipient in recipients.DistinctBy(u => u.Email, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await email.SendAsync(recipient.Email, subject, body, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send community join email to {Email}", recipient.Email);
            }
        }
    }

    private async Task<IReadOnlyList<User>> LoadModeratorRecipientsAsync(Community community, CancellationToken ct)
    {
        var moderatorIds = await db.CommunityMembers
            .AsNoTracking()
            .Where(m =>
                m.CommunityId == community.Id
                && m.Status == CommunityMemberStatuses.Approved
                && (m.Role == CommunityMemberRoles.Moderator || m.Role == CommunityMemberRoles.Creator))
            .Select(m => m.UserId)
            .ToListAsync(ct);

        var recipientIds = moderatorIds.ToHashSet();
        recipientIds.Add(community.CreatedByUserId);

        return await db.Users
            .AsNoTracking()
            .Where(u => recipientIds.Contains(u.Id) && u.Email != "")
            .ToListAsync(ct);
    }

    private string CommunityUrl(string routeSlug, string suffix = "") =>
        $"{emailOptions.Value.WebBaseUrl.TrimEnd('/')}/{routeSlug}{suffix}";
}
