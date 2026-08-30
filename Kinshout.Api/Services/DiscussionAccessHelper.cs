using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class DiscussionAccessHelper
{
    public static bool IsAuthor(Discussion discussion, Guid? userId) =>
        userId is not null && discussion.UserId == userId;

    public static bool IsApprovedParticipant(Discussion discussion, DiscussionParticipant? participant, Guid? userId)
    {
        if (userId is null)
            return false;

        if (IsAuthor(discussion, userId))
            return true;

        return participant is not null && participant.Status == CommunityMemberStatuses.Approved;
    }

    public static bool CanView(
        Discussion discussion,
        DiscussionParticipant? participant,
        Guid? userId,
        bool isApprovedCommunityMember = false)
    {
        if (discussion.CommunityId is not null && isApprovedCommunityMember)
            return true;

        if (CommunityVisibilityHelper.IsPrivate(discussion.Visibility))
            return IsApprovedParticipant(discussion, participant, userId);

        return true;
    }

    public static bool CanParticipate(Discussion discussion, DiscussionParticipant? participant, Guid userId) =>
        IsApprovedParticipant(discussion, participant, userId);

    public static bool CanModerateParticipants(
        Discussion discussion,
        Guid actorUserId,
        bool isCommunityModerator)
    {
        if (discussion.CommunityId is not null)
            return isCommunityModerator;

        return IsAuthor(discussion, actorUserId);
    }
}
