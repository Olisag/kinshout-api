using Kinshout.Api.Data;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class DiscussionVisibilityFilter
{
    /// <summary>
    /// Discussions anyone may see: public discussions in public communities.
    /// </summary>
    public static IQueryable<Discussion> WherePubliclyVisible(IQueryable<Discussion> query) =>
        query.Where(d =>
            d.Visibility == CommunityVisibilities.Public
            && (d.Community == null || d.Community.Visibility == CommunityVisibilities.Public));

    /// <summary>
    /// Discussions the viewer may see in feeds. A private community hides all of its
    /// discussions from non-members, even public ones; a private discussion additionally
    /// requires being its author, an approved participant, or an approved community member.
    /// </summary>
    public static IQueryable<Discussion> WhereVisibleTo(
        IQueryable<Discussion> query,
        KinshoutDbContext db,
        Guid? viewerUserId)
    {
        if (viewerUserId is null)
            return WherePubliclyVisible(query);

        return query.Where(d =>
            (d.Community == null
                || d.Community.Visibility == CommunityVisibilities.Public
                || db.CommunityMembers.Any(m =>
                    m.CommunityId == d.CommunityId
                    && m.UserId == viewerUserId
                    && m.Status == CommunityMemberStatuses.Approved)
                || (d.Community.CreatedByUserId == viewerUserId
                    && !db.CommunityMembers.Any(m =>
                        m.CommunityId == d.CommunityId && m.UserId == viewerUserId)))
            && (d.Visibility == CommunityVisibilities.Public
                || d.UserId == viewerUserId
                || d.Participants.Any(p =>
                    p.UserId == viewerUserId && p.Status == CommunityMemberStatuses.Approved)
                || (d.CommunityId != null && db.CommunityMembers.Any(m =>
                    m.CommunityId == d.CommunityId
                    && m.UserId == viewerUserId
                    && m.Status == CommunityMemberStatuses.Approved))
                || (d.Community != null
                    && d.Community.CreatedByUserId == viewerUserId
                    && !db.CommunityMembers.Any(m =>
                        m.CommunityId == d.CommunityId && m.UserId == viewerUserId))));
    }
}
