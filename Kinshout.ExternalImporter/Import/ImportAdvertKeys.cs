namespace Kinshout.ExternalImporter.Import;

internal static class ImportAdvertKeys
{
    public static string Format(string provider, string externalId) =>
        $"{NormalizeProvider(provider)}:{externalId.Trim()}";

    public static bool IsKnown(IReadOnlySet<string>? known, string provider, string? externalId) =>
        known is { Count: > 0 }
        && !string.IsNullOrWhiteSpace(externalId)
        && known.Contains(Format(provider, externalId));

    private static string NormalizeProvider(string provider) =>
        string.IsNullOrWhiteSpace(provider) ? "kinshout" : provider.Trim().ToLowerInvariant();
}
