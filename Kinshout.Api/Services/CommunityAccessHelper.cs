using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class CommunityAccessHelper
{
    public static bool IsApprovedMember(Community community, CommunityMember? membership, Guid? userId)
    {
        if (userId is null)
            return false;

        if (community.Slug == CommunityDefaults.GeneralSlug)
            return true;

        if (membership is not null && membership.Status == CommunityMemberStatuses.Approved)
            return true;

        // Legacy communities created before membership rows existed.
        return membership is null && community.CreatedByUserId == userId;
    }

    public static bool CanModerate(Community community, CommunityMember? membership, Guid? userId)
    {
        if (!community.IsActive || userId is null)
            return false;

        if (community.CreatedByUserId == userId)
            return true;

        return membership is not null
            && membership.Status == CommunityMemberStatuses.Approved
            && (membership.Role == CommunityMemberRoles.Creator
                || membership.Role == CommunityMemberRoles.Moderator);
    }

    public static bool CanViewMetadata(Community community, CommunityMember? membership, Guid? userId) =>
        !CommunityVisibilityHelper.IsPrivate(community.Visibility)
        || IsApprovedMember(community, membership, userId);

    public static bool CanViewDiscussions(Community community, CommunityMember? membership, Guid? userId) =>
        !CommunityVisibilityHelper.IsPrivate(community.Visibility)
        || IsApprovedMember(community, membership, userId);

    public static bool CanPost(Community community, CommunityMember? membership, Guid userId) =>
        community.IsActive && IsApprovedMember(community, membership, userId);

    public static bool IsGeneral(Community? community) =>
        community?.Slug == CommunityDefaults.GeneralSlug;

    /// <summary>
    /// Being in k/general (that is, being any user) must not reveal its private discussions.
    /// </summary>
    public static bool CanViewPrivateDiscussions(Community community, CommunityMember? membership, Guid? userId) =>
        !IsGeneral(community) && IsApprovedMember(community, membership, userId);
}
