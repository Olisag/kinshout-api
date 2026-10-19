using Kinshout.Api.Dtos;
using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class DiscussionJoinPrompt
{
    public const string CommunityMembershipRequired = "community_membership_required";
    public const string DiscussionMembershipRequired = "discussion_membership_required";

    /// <summary>
    /// Builds the prompt for a viewer who cannot read (<paramref name="toRead"/>) or cannot post in a discussion.
    /// <paramref name="communityRequired"/> means the community is private and the viewer is not an approved member.
    /// </summary>
    public static DiscussionJoinPromptDto Build(
        Discussion discussion,
        Community? community,
        Guid? viewerUserId,
        bool communityRequired,
        string? viewerStatus,
        bool toRead)
    {
        communityRequired &= community is not null;
        var requiresSignIn = viewerUserId is null;
        var status = viewerStatus is CommunityMemberStatuses.Pending or CommunityMemberStatuses.Rejected
            ? viewerStatus
            : null;
        var subject = communityRequired ? $"k/{community!.Slug}" : "cette discussion";
        var signIn = requiresSignIn ? "Connectez-vous puis rejoignez" : "Rejoignez";
        var goal = toRead ? "pour y accéder" : "pour publier";

        var message = status switch
        {
            CommunityMemberStatuses.Pending => $"Votre demande pour rejoindre {subject} est en attente d'approbation.",
            CommunityMemberStatuses.Rejected => $"Votre demande pour rejoindre {subject} a été refusée. Vous pouvez envoyer une nouvelle demande.",
            _ when communityRequired => $"{signIn} la communauté privée {subject} ou cette discussion {goal}.",
            _ when toRead => $"Cette discussion est privée. {signIn} la discussion {goal}.",
            _ => $"{signIn} la discussion {goal}.",
        };

        return new DiscussionJoinPromptDto(
            message,
            communityRequired ? CommunityMembershipRequired : DiscussionMembershipRequired,
            requiresSignIn,
            discussion.Id,
            community is null
                ? null
                : new DiscussionJoinPromptCommunityDto(community.Id, community.Slug, community.Name, community.Visibility),
            status,
            communityRequired ? $"/api/communities/{community!.Slug}/join" : null,
            $"/api/discussions/{discussion.Id}/join");
    }
}
