namespace Kinshout.ExternalImporter.Import;

public static class ImportDiscussionKeys
{
    public static string Format(string provider, string externalId) =>
        $"{provider.Trim()}:{externalId.Trim()}";

    public static bool IsKnown(IReadOnlySet<string> knownKeys, string provider, string externalId) =>
        knownKeys.Contains(Format(provider, externalId));

    public static IEnumerable<string> KnownExternalIdsForProvider(IReadOnlySet<string> knownKeys, string provider)
    {
        var prefix = $"{provider.Trim()}:";
        foreach (var key in knownKeys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return key[prefix.Length..];
        }
    }
}
