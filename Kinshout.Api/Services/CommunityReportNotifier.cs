using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public interface ICommunityReportNotifier
{
    Task NotifyUserReportedAsync(
        Community community,
        User reporter,
        User reported,
        UserReport report,
        CancellationToken ct = default);
}

public class CommunityReportNotifier(
    KinshoutDbContext db,
    IEmailService email,
    IOptions<EmailSettings> emailOptions,
    ILogger<CommunityReportNotifier> logger) : ICommunityReportNotifier
{
    public async Task NotifyUserReportedAsync(
        Community community,
        User reporter,
        User reported,
        UserReport report,
        CancellationToken ct = default)
    {
        var route = CommunitySlugHelper.ToRouteSlug(community.Slug);
        var slug = CommunitySlugHelper.Normalize(community.Slug);
        var recipients = await LoadModeratorRecipientsAsync(community, ct);
        var reasonLine = string.IsNullOrWhiteSpace(report.Reason)
            ? "Aucun motif précisé."
            : $"Motif : {report.Reason.Trim()}";

        var blockUrl =
            $"{emailOptions.Value.WebBaseUrl.TrimEnd('/')}/mod/{slug}/approved-users?action=block&userId={reported.Id}";

        var subject = $"Signalement — {route}";
        var body =
            $"""
            Bonjour,

            {reporter.DisplayName} ({reporter.Email}) a signalé {reported.DisplayName} ({reported.Email})
            dans la communauté {route} (« {community.Name} »).

            {reasonLine}

            Pour bloquer cet utilisateur de la communauté, ouvrez :
            {blockUrl}

            Ou utilisez l'action modérateur « Bloquer » sur la fiche membre.

            — Kinoiserie
            """;

        foreach (var recipient in recipients.DistinctBy(u => u.Email, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await email.SendAsync(recipient.Email, subject, body, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send user-report email to {Email}", recipient.Email);
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
}
