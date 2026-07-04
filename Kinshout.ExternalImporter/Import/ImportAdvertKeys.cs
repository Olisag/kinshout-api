namespace Kinshout.ExternalImporter.Import;

internal static class ImportAdvertKeys
{
    public static string Format(string provider, string externalId) =>
        $"{NormalizeProvider(provider)}:{externalId.Trim()}";

    public static bool IsKnown(IReadOnlySet<string>? known, string provider, string? externalId) =>
        known is { Count: > 0 }
        && !string.IsNullOrWhiteSpace(externalId)
        && known.Contains(Format(provider, externalId));

    public static IEnumerable<string> KnownExternalIdsForProvider(IReadOnlySet<string>? known, string provider)
    {
        if (known is null || known.Count == 0)
            yield break;

        var prefix = $"{NormalizeProvider(provider)}:";
        foreach (var key in known)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length)
                yield return key[prefix.Length..];
        }
    }

    private static string NormalizeProvider(string provider) =>
        string.IsNullOrWhiteSpace(provider) ? "kinshout" : provider.Trim().ToLowerInvariant();
}
