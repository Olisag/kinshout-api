using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public interface IDiscussionJoinNotifier
{
    Task NotifyJoinRequestAsync(Discussion discussion, User requester, CancellationToken ct = default);
    Task NotifyJoinApprovedAsync(Discussion discussion, User member, CancellationToken ct = default);
    Task NotifyJoinRejectedAsync(Discussion discussion, User member, CancellationToken ct = default);
}

public class DiscussionJoinNotifier(
    KinshoutDbContext db,
    IEmailService email,
    IOptions<EmailSettings> emailOptions,
    ILogger<DiscussionJoinNotifier> logger) : IDiscussionJoinNotifier
{
    public async Task NotifyJoinRequestAsync(Discussion discussion, User requester, CancellationToken ct = default)
    {
        var recipients = await LoadModeratorRecipientsAsync(discussion, ct);
        var subject = $"Demande d'accès — {discussion.Title}";
        var body =
            $"""
            Bonjour,

            {requester.DisplayName} ({requester.Email}) demande à rejoindre la discussion « {discussion.Title} ».

            Connectez-vous pour approuver ou refuser (une seule approbation suffit) :
            {DiscussionUrl(discussion.Id, "/members/pending")}

            — Kinoiserie
            """;

        await SendToManyAsync(recipients, subject, body, ct);
    }

    public async Task NotifyJoinApprovedAsync(Discussion discussion, User member, CancellationToken ct = default)
    {
        var subject = $"Accès accordé — {discussion.Title}";
        var body =
            $"""
            Bonjour {member.DisplayName},

            Votre demande pour rejoindre « {discussion.Title} » a été acceptée :
            {DiscussionUrl(discussion.Id)}

            — Kinoiserie
            """;

        await email.SendAsync(member.Email, subject, body, ct);
    }

    public async Task NotifyJoinRejectedAsync(Discussion discussion, User member, CancellationToken ct = default)
    {
        var subject = $"Demande refusée — {discussion.Title}";
        var body =
            $"""
            Bonjour {member.DisplayName},

            Votre demande pour rejoindre « {discussion.Title} » n'a pas été acceptée.

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
                logger.LogWarning(ex, "Failed to send discussion join email to {Email}", recipient.Email);
            }
        }
    }

    private async Task<IReadOnlyList<User>> LoadModeratorRecipientsAsync(Discussion discussion, CancellationToken ct)
    {
        var recipientIds = new HashSet<Guid>();

        if (discussion.CommunityId is Guid communityId)
        {
            var community = await db.Communities.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == communityId, ct);
            if (community is not null)
                recipientIds.Add(community.CreatedByUserId);

            var modIds = await db.CommunityMembers
                .AsNoTracking()
                .Where(m =>
                    m.CommunityId == communityId
                    && m.Status == CommunityMemberStatuses.Approved
                    && (m.Role == CommunityMemberRoles.Moderator || m.Role == CommunityMemberRoles.Creator))
                .Select(m => m.UserId)
                .ToListAsync(ct);
            foreach (var id in modIds)
                recipientIds.Add(id);
        }
        else
        {
            recipientIds.Add(discussion.UserId);
        }

        return await db.Users
            .AsNoTracking()
            .Where(u => recipientIds.Contains(u.Id) && u.Email != "")
            .ToListAsync(ct);
    }

    private string DiscussionUrl(Guid discussionId, string suffix = "") =>
        $"{emailOptions.Value.WebBaseUrl.TrimEnd('/')}/discussions/{discussionId}{suffix}";
}
