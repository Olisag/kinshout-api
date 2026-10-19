using Kinshout.Api.Models;

namespace Kinshout.Api.Services;

public static class CommunityVisibilityHelper
{
    public static bool TryNormalize(string? visibility, out string normalized)
    {
        normalized = (visibility ?? CommunityVisibilities.Public).Trim().ToLowerInvariant();
        return normalized is CommunityVisibilities.Public or CommunityVisibilities.Private;
    }

    public static bool IsPrivate(string? visibility) =>
        visibility?.Equals(CommunityVisibilities.Private, StringComparison.OrdinalIgnoreCase) == true;
}
