using Kinshout.Api.Dtos;

namespace Kinshout.Api.Services;

public class DiscussionAccessDeniedException(DiscussionJoinPromptDto joinPrompt)
    : UnauthorizedAccessException(joinPrompt.Message)
{
    public DiscussionJoinPromptDto JoinPrompt { get; } = joinPrompt;

    public DiscussionAccessDeniedDto ToResponse() => new(JoinPrompt.Message, JoinPrompt);
}
