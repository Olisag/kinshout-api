namespace Kinshout.Api.Auth;

public static class OriginMatcher
{
    public static string? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}";

        return value.TrimEnd('/');
    }

    public static bool IsAllowed(string? origin, IEnumerable<string> allowed)
    {
        if (allowed.Any(o => o.Trim() == "*"))
            return true;

        if (string.IsNullOrWhiteSpace(origin))
            return false;

        var normalized = NormalizeOrigin(origin);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return false;

        foreach (var entry in allowed)
        {
            var pattern = entry.Trim();
            var starIndex = pattern.IndexOf('*', StringComparison.Ordinal);
            if (starIndex >= 0)
            {
                var suffix = pattern[(starIndex + 1)..];
                if (suffix.StartsWith('.') && uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(NormalizeOrigin(pattern), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
